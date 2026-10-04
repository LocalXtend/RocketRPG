# RocketRPG mkxp-z agent (loaded through mkxp.json "preloadScript", before the game's own scripts).
#
# Talks to RocketRPG over a named pipe (ENV['RR_BRIDGE_PIPE']) with a tiny line protocol:
#   agent -> host : "<kind>\x1F<payload>\n"      (T telemetry, H heartbeat, M message, E esp, I tile, D data, N notice, L log)
#   host  -> agent: "<cmd>\x1F<arg>...\x1E<cmd>...\n" (reply to every line; may be empty)
# Newlines inside payloads are sent as \x1D.
#
# Everything here must be defensive: an exception from the agent must never reach the game.
# Reset (F12) and SystemExit are always allowed to propagate.

unless defined?(RocketBridge)
module RocketBridge
  SEP_CMD = "\x1E".freeze
  SEP_F   = "\x1F".freeze
  NL      = "\x1D".freeze

  @pipe = nil
  @next_connect = 0
  @frame = 0
  @rgss = (ENV['RR_RGSS'] || '3').to_i
  @speed = 1.0
  @base_rate = nil
  @paused = false
  @noclip = false
  @noclip_applied = false
  @auto = false
  @auto_speed = 1.0
  @skip = false
  @force_advance = false
  @esp = false
  @esp_vp = nil
  @esp_sprites = {}
  @esp_map = nil
  @bright = 1.0
  @bright_vp = nil
  @bright_sprite = nil
  @frames_fps = 0
  @frames_pipe = nil
  @frames_at = 0.0
  @font = nil
  @mouse = nil
  @msg_key = nil
  @wait_since = nil
  @last_t = nil
  @debug = (ENV['RR_AGENT_DEBUG'] == '1')

  class << self
    attr_reader :rgss

    # ---------------- pipe ----------------
    def connect
      return if @pipe || @frame < @next_connect
      name = ENV['RR_BRIDGE_PIPE']
      return if name.nil? || name.empty?
      begin
        @pipe = File.open("\\\\.\\pipe\\#{name}", 'r+b')
        @pipe.sync = true
        log("agent connected (RGSS#{@rgss})")
      rescue StandardError
        @pipe = nil
        @next_connect = @frame + 120
      end
    end

    def close_pipe
      begin
        @pipe.close if @pipe
      rescue StandardError
      end
      @pipe = nil
      @paused = false
      @next_connect = @frame + 120
    end

    def send_line(kind, payload = '')
      return unless @pipe
      begin
        line = kind + SEP_F + payload.to_s.gsub("\r", '').gsub("\n", NL)
        @pipe.write(line.b + "\n")
        # 카메라('C')는 매 프레임 보내므로 답을 기다리지 않습니다 (RocketRPG도 답하지 않음).
        return if kind == 'C'
        reply = @pipe.gets
        if reply.nil?
          close_pipe
          return
        end
        reply = reply.chomp.force_encoding('UTF-8')
        reply.split(SEP_CMD).each { |c| handle(c) } unless reply.empty?
      rescue StandardError
        close_pipe
      end
    end

    def log(msg)
      send_line('L', msg.to_s) if @pipe
    end

    def notice(msg)
      send_line('N', msg.to_s)
    end

    # ---------------- host commands ----------------
    def handle(c)
      kind, *args = c.split(SEP_F, -1)
      log("cmd #{kind} #{args.inspect}") if @debug
      case kind
      when 'eval'    then run_eval(args[0].to_s.gsub(NL, "\n"))
      when 'pause'   then @paused = (args[0] == '1')
      when 'speed'   then @speed = args[0].to_f; apply_speed
      when 'noclip'  then @noclip = (args[0] == '1'); @noclip_applied = false
      when 'auto'    then @auto = (args[0] == '1'); @auto_speed = [(args[1] || '1').to_f, 0.2].max
      when 'skip'    then @skip = (args[0] == '1')
      when 'advance' then @force_advance = true
      when 'esp'     then @esp = (args[0] == '1')
      when 'bright'  then @bright = args[0].to_f; apply_bright
      when 'font'    then @font = [args[0].to_s, args[1].to_i, args[2] == '1']; apply_font
      when 'mouse'   then @mouse = (args.size >= 2 && args[0] != '') ? [args[0].to_f, args[1].to_f] : nil
      when 'qsave'   then quick_save
      when 'qload'   then quick_load
      when 'dump'    then dump_data(args[0].to_i, args[1].to_i)
      when 'frames'  then set_frames(args[0].to_i, args[1].to_s)
      when 'espshare' then @esp_share = (args[0] == '1')
      when 'rctl'    then @rctl = (args[0] == '1'); RocketRemoteKeys.set([]) unless @rctl
      when 'rkeys'   then RocketRemoteKeys.set(args[0].to_s.split(',').map(&:to_i).select { |v| v > 0 && v < 256 }.first(32))
      end
    rescue StandardError, ScriptError => e
      log("cmd #{kind}: #{e.class}: #{e.message}")
    end

    # ---------------- 멀티 방장: 게임 화면을 RocketRPG로 ----------------
    # 방송하는 동안 fps에 맞춰 화면을 찍어(snap_to_bitmap) 따로 연 파이프로 보냅니다.
    # RocketRPG 창 위에 그리는 채팅·핑이 섞이지 않고, 방장만 보는 ESP·밝기 층은 찍는 동안 숨깁니다(화면에는 그대로).
    def set_frames(fps, pipe_name)
      close_frames if fps <= 0 || (pipe_name != '' && pipe_name != @frames_name)
      @frames_fps = fps
      return if fps <= 0 || @frames_pipe
      @frames_name = pipe_name
      @frames_pipe = File.open("\\\\.\\pipe\\#{pipe_name}", 'wb')
      @frames_pipe.sync = true
    rescue StandardError => e
      log("frames: #{e.class}: #{e.message}")
      close_frames
    end

    def close_frames
      begin
        @frames_pipe.close if @frames_pipe
      rescue StandardError
      end
      @frames_pipe = nil
      @frames_fps = 0
    end

    def send_frame
      return unless @frames_pipe
      # 일정한 간격의 예정 시각에 맞춰 찍음 (게임 40fps → 방송 30fps처럼 딱 나눠지지 않아도 고르게)
      now = Process.clock_gettime(Process::CLOCK_MONOTONIC)
      interval = 1.0 / @frames_fps
      return if now < @frames_at - 0.004
      @frames_at = now - @frames_at > interval ? now + interval : @frames_at + interval
      # 참가자도 도구 권한이 있으면(espshare) ESP는 방송 화면에 남깁니다. 밝기 층은 방장 화면 설정이라 늘 숨김.
      hidden = (@esp_share ? [@bright_vp] : [@esp_vp, @bright_vp]).select { |v| v && !v.disposed? && v.visible }
      hidden.each { |v| v.visible = false }
      begin
        bmp = Graphics.snap_to_bitmap
      ensure
        hidden.each { |v| v.visible = true }
      end
      data = bmp.raw_data
      w = bmp.width
      h = bmp.height
      bmp.dispose
      @frames_pipe.write([0x52524652, w, h, data.bytesize].pack('L<4'))
      @frames_pipe.write(data)
    rescue StandardError => e
      log("frames: #{e.class}: #{e.message}")
      close_frames
    end

    def run_eval(code)
      eval(code, TOPLEVEL_BINDING)
    rescue StandardError, ScriptError => e
      log("eval: #{e.class}: #{e.message}")
    end

    # ---------------- per-frame ----------------
    def tick
      @frame += 1
      RocketInputHook.ensure if @frame % 60 == 1
      connect if @pipe.nil?
      return unless @pipe
      if @frame == 2
        log("font hook: #{@font_error}") if @font_error
        if @debug
          fx = Font.new
          log("font: default_name=#{Font.default_name.inspect} new=#{fx.name.inspect} exist(바탕)=#{Font.exist?('바탕')} exist(batang)=#{Font.exist?('batang')} exist(malgun gothic)=#{Font.exist?('Malgun Gothic')}")
        end
        apply_font
        apply_speed
      end
      apply_noclip
      track_message
      track_choice if @frame % 3 == 1
      # 파이프 왕복(보내고 답 기다리기)은 게임 루프를 잠깐 멈추므로 최소로: 6프레임마다 텔레메트리(바뀐 경우) 또는
      # 하트비트 한 번. 답에 실려 오는 명령은 이 왕복으로 받습니다. (ESP는 게임 화면에 직접 그리므로 보내지 않습니다)
      sent = false
      if @frame % 3 == 0
        if @mouse && @frame % 6 == 3
          send_tile
          sent = true
        end
        if @frame % 6 == 0
          t = telemetry
          if t != @last_t
            @last_t = t
            send_line('T', t)
            sent = true
          end
          send_line('H') unless sent
          sent = true
        end
      end
      # 멀티 참가자가 조종 중이면 매 프레임 한 번 왕복해 키 상태를 바로 받습니다.
      send_line('H') if @rctl && !sent
      RocketRemoteKeys.frame
      if @debug
        t0 = Process.clock_gettime(Process::CLOCK_MONOTONIC)
        update_esp if @esp || @esp_vp
        t1 = Process.clock_gettime(Process::CLOCK_MONOTONIC)
        @perf_esp = (@perf_esp || 0) + (t1 - t0)
        @perf_frames = (@perf_frames || 0) + 1
        @perf_start ||= t1
        if @perf_frames >= 300
          log(format('perf: esp %.2fms/frame, fps %.1f, sprites %d', @perf_esp * 1000 / @perf_frames, @perf_frames / (t1 - @perf_start), @esp_sprites.size))
          @perf_esp = 0; @perf_frames = 0; @perf_start = t1
        end
      else
        update_esp if @esp || @esp_vp
      end
      if @bright_sprite && @frame % 60 == 0
        apply_bright if @bright_sprite.disposed? || (@bright_vp && @bright_vp.disposed?)
      end
      send_frame if @frames_fps > 0
      pause_loop if @paused
    end

    def pause_loop
      while @paused && @pipe
        Graphics.rr_bridge_update
        send_line('H')
      end
    end

    # ---------------- helpers ----------------
    def scene
      if @rgss >= 3 && defined?(SceneManager)
        SceneManager.scene
      else
        $scene
      end
    end

    def on_map?
      s = scene
      defined?(Scene_Map) && s.is_a?(Scene_Map)
    end

    def jstr(s)
      s.to_s.gsub('\\', '\\\\\\\\').gsub('"', '\\"').gsub("\n", '\\n').gsub("\r", '')
    end

    def telemetry
      m = $game_map
      p = $game_player
      name = (scene ? scene.class.name : '') rescue ''
      mid = (m ? m.map_id : 0) rescue 0
      px = (p ? p.x : 0) rescue 0
      py = (p ? p.y : 0) rescue 0
      dx = (m ? m.display_x : 0) rescue 0
      dy = (m ? m.display_y : 0) rescue 0
      th = (p && p.instance_variable_get(:@through)) ? 'true' : 'false'
      "{\"Scene\":\"#{jstr(name)}\",\"MapId\":#{mid},\"PlayerX\":#{px},\"PlayerY\":#{py}," \
        "\"DisplayX\":#{dx},\"DisplayY\":#{dy},\"Noclip\":#{th},\"ScreenW\":#{Graphics.width},\"ScreenH\":#{Graphics.height}}"
    end

    # ---------------- speed / noclip ----------------
    def apply_speed
      return unless defined?(Graphics) && Graphics.respond_to?(:frame_rate=)
      @base_rate ||= Graphics.frame_rate
      rate = (@base_rate * @speed).round
      rate = 10 if rate < 10
      rate = 480 if rate > 480
      Graphics.frame_rate = rate
    rescue StandardError
    end

    def apply_noclip
      p = $game_player
      return unless p
      if @noclip
        p.instance_variable_set(:@through, true)
        @noclip_applied = true
      elsif !@noclip_applied
        p.instance_variable_set(:@through, false)
        @noclip_applied = true
      end
    rescue StandardError
    end

    # ---------------- messages (Ren'Py bar, auto/skip) ----------------
    def message_text
      case @rgss
      when 1
        t = $game_temp ? $game_temp.message_text : nil
        [!t.nil?, t.to_s]
      when 2
        gm = $game_message
        return [false, ''] unless gm
        [gm.visible ? true : false, (gm.texts || []).join("\n")]
      else
        gm = $game_message
        return [false, ''] unless gm
        [gm.visible ? true : false, gm.respond_to?(:all_text) ? gm.all_text.to_s : (gm.texts || []).join("\n")]
      end
    rescue StandardError
      [false, '']
    end

    def clean(t)
      t = t.to_s.dup
      t.gsub!(/\\[Vv]\[(\d+)\]/) { ($game_variables ? $game_variables[$1.to_i] : '').to_s } rescue nil
      t.gsub!(/\\[Nn]\[(\d+)\]/) { (a = ($game_actors ? $game_actors[$1.to_i] : nil)) ? a.name : '' } rescue nil
      t.gsub!(/\\[A-Za-z]+\[[^\]]*\]/, '')
      t.gsub!(/\\[.|!><^{}$]/, '')
      t.gsub!(/\\\\/, '\\')
      t
    end

    # 선택지 투표(멀티): 선택지가 떠 있으면 글을, 사라지면 끝을 알림. "Q" 줄 = 번호 ␟ -1 ␟ 줄마다 "1"+글
    def choice_items
      case @rgss
      when 1
        t = $game_temp
        return nil unless t && t.choice_max.to_i > 0 && t.message_text
        lines = t.message_text.to_s.split("\n")
        lines[t.choice_start.to_i, t.choice_max.to_i]
      when 2
        gm = $game_message
        return nil unless gm && gm.visible && gm.choice_max.to_i > 0
        (gm.texts || [])[gm.choice_start.to_i, gm.choice_max.to_i]
      else
        gm = $game_message
        return nil unless gm && gm.choice?
        w = message_window
        cw = w ? w.instance_variable_get(:@choice_window) : nil
        return nil if cw && !(cw.active || cw.open?)
        gm.choices
      end
    rescue StandardError
      nil
    end

    def track_choice
      items = choice_items
      key = items ? items.join("\n") : nil
      return if key == @choice_key
      if @choice_key && items.nil?
        send_line('Q', "#{@choice_gen}#{SEP_F}-1#{SEP_F}")
      elsif items
        @choice_gen = (@choice_gen || 0) + 1
        send_line('Q', "#{@choice_gen}#{SEP_F}-1#{SEP_F}" + items.map { |c| '1' + clean(c.to_s).gsub("\n", ' ') }.join("\n"))
      end
      @choice_key = key
    end

    def track_message
      busy, text = message_text
      key = busy ? text : nil
      auto_diagnose if key && key == @msg_key
      return if key == @msg_key
      @c_polls = 0
      @diagnosed = false
      @msg_key = key
      @wait_since = nil
      @msg_since = busy ? Time.now : nil
      send_line('M', (busy ? '1' : '0') + SEP_F + SEP_F + (busy ? clean(text) : ''))
    end

    # 자동 넘김이 켜졌는데 한 대사가 오래 넘어가지 않으면 원인을 한 번 기록합니다 (특이한 메시지 구조 진단용).
    def auto_diagnose
      return if @diagnosed || !@auto || @skip || @msg_since.nil?
      return if Time.now - @msg_since < auto_wait + 5
      @diagnosed = true
      w = message_window
      log("auto stalled: scene=#{scene.class} win=#{w.class} waiting=#{waiting_for_input?} " \
          "choice=#{choice_active?} c_polls=#{@c_polls.to_i}")
    rescue StandardError
    end

    def message_window
      s = scene
      s ? s.instance_variable_get(:@message_window) : nil
    rescue StandardError
      nil
    end

    def choice_active?
      case @rgss
      when 1
        t = $game_temp
        t && (t.choice_max.to_i > 0 || t.num_input_variable_id.to_i > 0)
      when 2
        gm = $game_message
        gm && (gm.choice_max.to_i > 0 || gm.num_input_variable_id.to_i > 0)
      else
        gm = $game_message
        gm && (gm.choice? || gm.num_input? || gm.item_choice?)
      end
    rescue StandardError
      true
    end

    def waiting_for_input?
      w = message_window
      return false unless w
      if @rgss == 1
        w.instance_variable_get(:@contents_showing) ? true : false
      else
        w.pause ? true : false
      end
    rescue StandardError
      false
    end

    def auto_wait
      len = clean(message_text[1]).length
      [[1.1 + len * 0.03, 0.75].max, 4.5].min / @auto_speed
    end

    # Input.trigger?(C) 가로채기: 대화창에서만, 선택지/숫자 입력 중에는 절대 누르지 않음
    # 진짜 키처럼 "누른 프레임" 동안에는 모든 호출에 true를 돌려줍니다. 한 번만 true를 주면, 같은 프레임에
    # 먼저 C를 묻는 다른 스크립트(HUD, 단축키 등)가 그 입력을 가져가 대화창이 넘어가지 않았습니다.
    def want_confirm?(key)
      return false unless @auto || @skip || @force_advance
      return false unless key == Input::C || key == :C
      @c_polls = (@c_polls || 0) + 1
      return true if @press_frame == @frame
      return false unless @msg_key
      return false if choice_active?
      if @force_advance
        @force_advance = false
        return press_now
      end
      return true if @skip
      # 표준 메시지 창을 찾지 못하는 게임(메시지 스크립트가 창을 바꿔 둔 경우)은 창 상태 대신
      # "대화 중" 데이터만 보고 자동 대기 시간마다 확인을 누릅니다 (사람이 누르는 것과 같음).
      if waiting_for_input? || message_window.nil?
        @wait_since ||= Time.now
        if Time.now - @wait_since >= auto_wait
          @wait_since = nil
          return press_now
        end
      elsif @msg_since && Time.now - @msg_since >= auto_wait + 1.5
        # 메시지 창을 바꿔 둔 게임(입력 대기 상태를 알 수 없음): 글자가 다 나왔을 만큼 기다린 뒤 누릅니다.
        @msg_since = Time.now
        return press_now
      end
      false
    rescue StandardError
      false
    end

    def press_now
      @press_frame = @frame
      true
    end

    # ---------------- ESP / tile inspector ----------------
    def event_name(ev)
      e = ev.instance_variable_get(:@event)
      n = e ? e.name.to_s : ''
      n.empty? ? format('EV%03d', ev.id) : n
    rescue StandardError
      ''
    end

    # ESP는 게임 화면에 직접 그립니다 (이벤트마다 작은 스프라이트, 위치만 매 프레임 갱신).
    # 예전에는 RocketRPG 창 위 투명 창에 그려서, 걸을 때 매 프레임 창 전체를 다시 합성하느라 크게 버벅였습니다.
    ESP_COLORS = [[0, 153, 255], [0, 204, 68], [255, 136, 0], [187, 51, 255], [0, 255, 255]].freeze

    def camera
      m = $game_map
      [m.display_x.to_f / tile_scale, m.display_y.to_f / tile_scale]
    end

    def loop_x?
      $game_map.respond_to?(:loop_horizontal?) && $game_map.loop_horizontal? ? true : false
    rescue StandardError
      false
    end

    def loop_y?
      $game_map.respond_to?(:loop_vertical?) && $game_map.loop_vertical? ? true : false
    rescue StandardError
      false
    end

    def esp_class(trigger)
      case trigger
      when 0 then 0
      when 1, 2 then 1
      when 3 then 2
      when 4 then 3
      else 4
      end
    end

    def make_esp_sprite(ev)
      @esp_measure ||= Bitmap.new(1, 1)
      @esp_measure.font.size = 12
      name = event_name(ev)
      tw = @esp_measure.text_size(name).width + 4
      lh = 14
      w = [32, tw].max
      bmp = Bitmap.new(w, 32 + lh)
      bmp.font.size = 12
      r, g, b = ESP_COLORS[esp_class((ev.trigger rescue 0).to_i)]
      bx = (w - 32) / 2
      bmp.fill_rect(bx, lh, 32, 32, Color.new(r, g, b, 60))
      edge = Color.new(r, g, b)
      bmp.fill_rect(bx, lh, 32, 2, edge)
      bmp.fill_rect(bx, lh + 30, 32, 2, edge)
      bmp.fill_rect(bx, lh + 2, 2, 28, edge)
      bmp.fill_rect(bx + 30, lh + 2, 2, 28, edge)
      lx = (w - tw) / 2
      bmp.fill_rect(lx, 0, tw, lh, Color.new(0, 0, 0, 170))
      bmp.draw_text(lx, -1, tw, lh + 2, name, 1)
      sp = Sprite.new(@esp_vp)
      sp.bitmap = bmp
      sp.ox = bx
      sp.oy = lh
      sp
    end

    def dispose_esp
      @esp_sprites.each_value do |sp|
        next if sp.disposed?
        sp.bitmap.dispose if sp.bitmap && !sp.bitmap.disposed?
        sp.dispose
      end
      @esp_sprites = {}
      @esp_vp.dispose if @esp_vp && !@esp_vp.disposed?
      @esp_vp = nil
      @esp_map = nil
    end

    def update_esp
      unless @esp
        dispose_esp
        return
      end
      unless on_map? && $game_map
        @esp_vp.visible = false if @esp_vp && !@esp_vp.disposed?
        return
      end
      m = $game_map
      dispose_esp if @esp_vp.nil? || @esp_vp.disposed? || @esp_map != m.map_id || @esp_events != m.events.object_id
      unless @esp_vp
        @esp_vp = Viewport.new(0, 0, Graphics.width, Graphics.height)
        @esp_vp.z = 100_000
        @esp_map = m.map_id
        @esp_events = m.events.object_id
      end
      @esp_vp.visible = true
      gw = Graphics.width
      gh = Graphics.height
      cx, cy = camera
      lx = loop_x?
      ly = loop_y?
      s = tile_scale
      made = 0
      m.events.each do |id, ev|
        sp = @esp_sprites[id]
        dx = ev.real_x.to_f / s - cx
        dy = ev.real_y.to_f / s - cy
        dx = (dx + 4) % m.width - 4 if lx
        dy = (dy + 4) % m.height - 4 if ly
        x = (dx * 32).round
        y = (dy * 32).round
        vis = x > -48 && y > -48 && x < gw + 16 && y < gh + 16 && !(ev.instance_variable_get(:@erased))
        unless sp
          # 화면에 들어온 이벤트만, 한 프레임에 몇 개씩 만듭니다 (이벤트가 많은 맵에서 첫 프레임 끊김 방지)
          next if !vis || made >= 8
          sp = @esp_sprites[id] = make_esp_sprite(ev)
          made += 1
        end
        sp.visible = vis
        next unless vis
        sp.x = x
        sp.y = y
      end
    rescue StandardError => e
      log("esp: #{e.message}")
      dispose_esp
      @esp = false
    end

    def tile_scale
      @rgss == 1 ? 128.0 : (@rgss == 2 ? 256.0 : 1.0)
    end

    def send_tile
      return unless on_map? && $game_map
      m = $game_map
      mx = (m.display_x / tile_scale + @mouse[0] / 32.0).floor
      my = (m.display_y / tile_scale + @mouse[1] / 32.0).floor
      mx %= m.width if m.respond_to?(:loop_horizontal?) && m.loop_horizontal?
      my %= m.height if m.respond_to?(:loop_vertical?) && m.loop_vertical?
      valid = mx >= 0 && my >= 0 && mx < m.width && my < m.height
      pass = false
      tiles = []
      evs = []
      if valid
        pass = case @rgss
               when 1 then m.passable?(mx, my, 0)
               when 2 then m.passable?(mx, my)
               else [2, 4, 6, 8].any? { |d| m.passable?(mx, my, d) }
               end
        3.times { |z| tiles << (m.data[mx, my, z] rescue 0).to_i }
        m.events.each_value { |ev| evs << event_name(ev) if ev.x == mx && ev.y == my }
      end
      json = "{\"mapX\":#{mx},\"mapY\":#{my},\"passable\":#{pass ? 'true' : 'false'}," \
             "\"tileIds\":[#{tiles.join(',')}],\"events\":\"#{jstr(evs.empty? ? '없음' : evs.join(', '))}\"," \
             "\"screenX\":#{@mouse[0]},\"screenY\":#{@mouse[1]}}"
      send_line('I', json)
    rescue StandardError => e
      log("tile: #{e.message}")
    end

    def dump_data(max_sw, max_va)
      sw = $game_switches ? (1..max_sw).map { |i| $game_switches[i] ? '1' : '0' }.join : ''
      va = $game_variables ? (1..max_va).map { |i| $game_variables[i].to_s.tr(';|', ',/') }.join(';') : ''
      send_line('D', "#{sw}|#{va}")
    end

    # ---------------- brightness / font ----------------
    def dispose_bright
      @bright_sprite.bitmap.dispose if @bright_sprite && !@bright_sprite.disposed? && @bright_sprite.bitmap
      @bright_sprite.dispose if @bright_sprite && !@bright_sprite.disposed?
      @bright_vp.dispose if @bright_vp && !@bright_vp.disposed?
      @bright_sprite = nil
      @bright_vp = nil
    rescue StandardError
      @bright_sprite = nil
      @bright_vp = nil
    end

    def apply_bright
      dispose_bright
      b = @bright
      return if (b - 1.0).abs < 0.01
      @bright_vp = Viewport.new(0, 0, Graphics.width, Graphics.height)
      @bright_vp.z = 99_999
      @bright_sprite = Sprite.new(@bright_vp)
      bmp = Bitmap.new(Graphics.width, Graphics.height)
      if b < 1.0
        bmp.fill_rect(bmp.rect, Color.new(0, 0, 0))
        @bright_sprite.opacity = ((1.0 - b) * 255).round.clamp(0, 240)
      else
        bmp.fill_rect(bmp.rect, Color.new(255, 255, 255))
        @bright_sprite.blend_type = 1
        @bright_sprite.opacity = ((b - 1.0) * 70).round.clamp(0, 200)
      end
      @bright_sprite.bitmap = bmp
    rescue StandardError => e
      log("bright: #{e.message}")
    end

    def apply_font
      return unless @font
      name, size, bold = @font
      unless name.nil? || name.empty?
        Font.default_name = [name, 'Malgun Gothic', '맑은 고딕', 'Arial']
      end
      Font.default_size = size if size > 0
      Font.default_bold = true if bold
    rescue StandardError => e
      log("font: #{e.message}")
    end

    # ---------------- quick save / load (slot 1) ----------------
    def quick_save
      case @rgss
      when 1
        File.open('Save1.rxdata', 'wb') { |f| Scene_Save.new.write_save_data(f) }
      when 2
        File.open('Save1.rvdata', 'wb') { |f| Scene_File.new(true, false, false).write_save_data(f) }
      else
        raise 'save failed' unless DataManager.save_game(0)
      end
      Sound.play_save rescue nil
      notice('1번 슬롯에 퀵 세이브 완료')
    rescue StandardError, ScriptError => e
      notice("퀵 세이브 실패: #{e.message}")
    end

    # 퀵 로드는 장면 경계에서 합니다. 맵 프레임 도중에 데이터를 바꾸고 $scene = Scene_Map.new 를 하면
    # 그 프레임의 나머지(Game_Player#update 등)가 아직 스프라이트가 없는 새 장면을 건드려, 파티 행렬(Train_Actor) 같은
    # 스크립트가 NoMethodError로 게임을 끝냈습니다. 원래 불러오기 화면처럼 현재 장면을 끝낸 뒤 불러옵니다.
    def quick_load
      file = { 1 => 'Save1.rxdata', 2 => 'Save1.rvdata' }[@rgss]
      exists = file ? FileTest.exist?(file) : (DataManager.save_file_exists? rescue true)
      raise '1번 슬롯에 저장 데이터가 없습니다' unless exists
      loader = RocketQuickLoadScene.new { perform_quick_load }
      if @rgss <= 2
        $scene = loader
      else
        SceneManager.instance_variable_set(:@scene, loader)
      end
    rescue StandardError, ScriptError => e
      notice("퀵 로드 실패: #{e.message}")
    end

    def perform_quick_load
      case @rgss
      when 1
        File.open('Save1.rxdata', 'rb') { |f| Scene_Load.new.read_save_data(f) }
        $game_system.bgm_play($game_system.playing_bgm) rescue nil
        $game_system.bgs_play($game_system.playing_bgs) rescue nil
        $game_map.update rescue nil
        $scene = Scene_Map.new
      when 2
        sf = Scene_File.new(false, false, false)
        File.open('Save1.rvdata', 'rb') { |f| sf.read_save_data(f) }
        (sf.instance_variable_get(:@last_bgm).play rescue nil)
        (sf.instance_variable_get(:@last_bgs).play rescue nil)
        $scene = Scene_Map.new
      else
        raise '1번 슬롯에 저장 데이터가 없습니다' unless DataManager.load_game(0)
        $game_system.on_after_load rescue nil
        SceneManager.goto(Scene_Map)
      end
      Sound.play_load rescue nil
      notice('1번 슬롯 퀵 로드 완료')
    rescue StandardError, ScriptError => e
      notice("퀵 로드 실패: #{e.message}")
      # 불러오기에 실패하면 원래 게임(맵)으로 돌아갑니다
      if @rgss <= 2
        $scene = Scene_Map.new if $scene.is_a?(RocketQuickLoadScene)
      else
        SceneManager.goto(Scene_Map) if SceneManager.scene.is_a?(RocketQuickLoadScene)
      end
    end
  end
end

# 최초 글꼴 (게임 스크립트가 로드되기 전에 적용)
begin
  if (f = ENV['RR_FONT']) && !f.empty?
    name, size, bold = f.split('|')
    Font.default_name = [name, 'Malgun Gothic', '맑은 고딕', 'Arial'] unless name.to_s.empty?
    Font.default_size = size.to_i if size.to_i > 0
    Font.default_bold = true if bold == '1'
    RocketBridge.instance_variable_set(:@font, [name.to_s, size.to_i, bold == '1'])
    unless name.to_s.empty?
      # 사용자가 글꼴을 골랐으면, 게임 스크립트가 글꼴 이름을 직접 정해도(Font.default_name=, font.name=, Font.new) 그 글꼴을 씁니다.
      # 글꼴 대체표(fontSub)는 스크립트에서 찾은 이름만 바꿀 수 있어 빠지는 게임이 있었습니다.
      RR_USER_FONT = [name.to_s, 'Malgun Gothic', '맑은 고딕', 'Arial'].freeze
      class Font
        class << self
          alias_method :rr_default_name_set, :default_name=
          def default_name=(_value)
            rr_default_name_set(RR_USER_FONT)
          end
        end
        alias_method :rr_name_set, :name=
        def name=(_value)
          rr_name_set(RR_USER_FONT)
        end
        alias_method :rr_font_initialize, :initialize
        def initialize(*args)
          rr_font_initialize(*args)
          rr_name_set(RR_USER_FONT)
        end
      end
      Font.default_name = RR_USER_FONT
    end
  end
rescue StandardError, ScriptError => e
  RocketBridge.instance_variable_set(:@font_error, "#{e.class}: #{e.message}")
end
module Graphics
  class << self
    unless method_defined?(:rr_bridge_update)
      alias_method :rr_bridge_update, :update
      def update
        begin
          RocketBridge.tick
        rescue StandardError, ScriptError
        end
        rr_bridge_update
      end
    end
    # RocketRPG 창 안에 임베드되어 있으므로 게임 스크립트가 전체 화면으로 바꾸지 못하게 합니다 (끄는 것은 허용).
    if method_defined?(:fullscreen=) && !method_defined?(:rr_bridge_fullscreen_set)
      alias_method :rr_bridge_fullscreen_set, :fullscreen=
      def fullscreen=(value)
        rr_bridge_fullscreen_set(false) unless value
        value
      end
    end
  end
end

# 퀵 로드용 1회성 장면: 현재 장면이 끝난 뒤 $scene.main(RGSS1/2) 또는 SceneManager.run(Ace)이 부릅니다.
class RocketQuickLoadScene
  def initialize(&blk)
    @blk = blk
  end

  def main
    @blk.call
  end
end

# Input.trigger? 감싸기. 게임 스크립트는 이 프리로드보다 나중에 실행되므로, 자체 입력 모듈
# ("전체키 입력 확장" 등 GetAsyncKeyState 기반)이 Input.trigger?를 새로 정의하면 우리 감싸기가 사라집니다.
# 그래서 매 초 확인해서, 다른 정의로 바뀌었으면 그 위에 다시 감쌉니다 (자동 진행/스킵이 그런 게임에서도 동작).

# RocketRPG의 입력 감싸기들(에이전트, 자동 테스트)이 서로를 "게임이 바꿔 놓은 것"으로 보고 번갈아 계속 감싸면
# 호출 사슬이 수백 겹으로 쌓여 네이티브 스택이 넘쳐 mkxp-z가 죽었습니다. 각 감싸기가 무엇을 감쌌는지 기록해 두고,
# 내 감싸기가 사슬 어딘가에 남아 있으면 다시 감싸지 않습니다 (게임 코드가 바꿔 놓았을 때만 다시 감쌈).
unless defined?(RocketInputChain)
module RocketInputChain
  WRAPPED = {}   # 우리 감싸기 Method => 그것이 감싼 Method
  def self.contains?(meth, mine)
    40.times do
      return true if meth == mine
      nxt = WRAPPED[meth]
      return false unless nxt
      meth = nxt
    end
    false
  end
end
end

# 멀티: 참가자가 누르고 있는 키 (윈도우 가상 키, RocketRPG가 매 프레임 알려 줌).
# 창 메시지로 넣은 Shift는 SDL이 실제 키보드 상태를 보고 바로 떼어 버리고, "전체키 입력" 스크립트는 GetAsyncKeyState로
# 실제 키보드만 읽어 참가자 키가 보이지 않았습니다. 그래서 Shift(A 버튼)와 Win32API 키 상태에 참가자 키를 더합니다.
module RocketRemoteKeys
  SHIFTS = [0x10, 0xA0, 0xA1]
  @keys = []
  @cur = []
  @prev = []
  class << self
    def set(list)
      @keys = list
    end

    # Graphics.update마다 (에이전트 tick): 이번 프레임 상태와 지난 프레임 상태
    def frame
      @prev = @cur
      @cur = @keys
    end

    def any?
      !@cur.empty?
    end

    def held?(vk)
      @cur.include?(vk) || (vk == 0x10 && (@cur & SHIFTS).any?) || ((vk == 0xA0 || vk == 0xA1) && @cur.include?(0x10))
    end

    def fresh?(vk)
      held?(vk) && !(@prev.include?(vk) || (vk == 0x10 && (@prev & SHIFTS).any?))
    end

    def shift_button?(k)
      k == Input::A || k == :A || k == :SHIFT
    rescue StandardError
      false
    end
  end
end

module RocketInputHook
  @n = 0
  @mine = {}
  def self.ensure
    wrap(:trigger?) do |orig, args|
      RocketBridge.want_confirm?(args[0]) ||
        (RocketRemoteKeys.any? && RocketRemoteKeys.shift_button?(args[0]) && RocketRemoteKeys.fresh?(0x10)) ||
        Input.send(orig, *args)
    end
    wrap(:press?) do |orig, args|
      (RocketRemoteKeys.any? && RocketRemoteKeys.shift_button?(args[0]) && RocketRemoteKeys.held?(0x10)) || Input.send(orig, *args)
    end
    wrap(:repeat?) do |orig, args|
      (RocketRemoteKeys.any? && RocketRemoteKeys.shift_button?(args[0]) && RocketRemoteKeys.fresh?(0x10)) || Input.send(orig, *args)
    end
  end

  def self.wrap(name, &blk)
    cur = Input.method(name)
    return if @mine[name] && RocketInputChain.contains?(cur, @mine[name])
    @n += 1
    orig = :"rr_bridge_#{name.to_s.chomp('?')}_#{@n}?"
    sc = Input.singleton_class
    sc.send(:alias_method, orig, name)
    sc.send(:define_method, name) { |*args| blk.call(orig, args) }
    @mine[name] = Input.method(name)
    RocketInputChain::WRAPPED[@mine[name]] = cur
  rescue StandardError
  end
end
RocketInputHook.ensure

# Win32API 키 상태 (GetAsyncKeyState / GetKeyState / GetKeyboardState)에 참가자 키를 더함
if defined?(Win32API) && !Win32API.method_defined?(:rr_remote_call)
  class Win32API
    alias_method :rr_remote_init, :initialize
    def initialize(dll, func, *rest)
      @rr_func = func.to_s.downcase.sub(/[aw]\z/, '')
      rr_remote_init(dll, func, *rest)
    end

    alias_method :rr_remote_call, :call
    def call(*args)
      r = rr_remote_call(*args)
      return r unless RocketRemoteKeys.any?
      case @rr_func
      when 'getasynckeystate'
        vk = args[0].to_i
        r = r.to_i | 0x8000 | (RocketRemoteKeys.fresh?(vk) ? 1 : 0) if RocketRemoteKeys.held?(vk)
      when 'getkeystate'
        vk = args[0].to_i
        r = r.to_i | 0x8000 if RocketRemoteKeys.held?(vk)
      when 'getkeyboardstate'
        buf = args[0]
        if buf.is_a?(String) && buf.bytesize >= 256
          (1..255).each { |vk| buf.setbyte(vk, buf.getbyte(vk) | 0x80) if RocketRemoteKeys.held?(vk) }
        end
      end
      r
    rescue StandardError
      r
    end
  end
end
end
