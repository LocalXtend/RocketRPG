# RocketRPG mkxp-z automated compatibility test (loaded only when ENV['RR_AUTOTEST'] is a log file path).
#
# Drives the game without a person: confirms through the title / messages, starts a new game, then teleports
# through every map (placing the player next to an event so the confirm key interacts with it) and walks
# around, letting BGM / autorun / parallel / touch events play on each. Choices are answered differently each
# time so "wrong answer → repeat" loops do not trap the run, and the per-map timer always moves on.
# If a custom title never reacts to the confirm key, a new game is started through the game's own scripts.
# Progress goes to the log file (scripts/mkxp_autotest.ps1 reads it; script errors show up as mkxp-z's box).

unless defined?(RocketAutotest)
module RocketAutotest
  @path = ENV['RR_AUTOTEST'].to_s
  @rgss = (ENV['RR_RGSS'] || '3').to_i
  @max_maps = (ENV['RR_AUTOTEST_MAX'] || '60').to_i
  @per_map = (ENV['RR_AUTOTEST_FRAMES'] || '150').to_i
  @frame = 0
  @phase = :boot
  @maps = nil
  @index = -1
  @map_frames = 0
  @off_map = 0
  @target = nil
  @done = false
  @last_scene = nil
  @scene_logs = 0
  @forced_new = false

  class << self
    def log(msg)
      File.open(@path, 'a') { |f| f.write("#{@frame}\t#{msg}\n") }
    rescue StandardError
    end

    def scene
      (@rgss >= 3 && defined?(SceneManager)) ? SceneManager.scene : $scene
    end

    def scene_name
      s = scene
      s ? s.class.name : 'nil'
    rescue StandardError
      '?'
    end

    def on_map?
      defined?(Scene_Map) && scene.is_a?(Scene_Map)
    end

    def in_battle?
      defined?(Scene_Battle) && scene.is_a?(Scene_Battle)
    end

    def ext
      @rgss == 1 ? 'rxdata' : (@rgss == 2 ? 'rvdata' : 'rvdata2')
    end

    # 실제 플레이로 갈 수 있는 맵만: 시작 맵 + 이벤트/공통 이벤트의 "장소 이동" 명령이 가리키는 맵.
    # (개발용 테스트 맵처럼 게임에서 갈 일이 없는 맵에 억지로 들어가 생기는 가짜 오류를 피합니다.)
    # 도착 위치도 그 명령의 좌표를 그대로 씁니다.
    def map_list
      infos = load_data("Data/MapInfos.#{ext}")
      @dests = {}
      scan = lambda do |list|
        (list || []).each do |c|
          next unless c.code == 201 && c.parameters[0] == 0
          id, x, y, d = c.parameters[1], c.parameters[2], c.parameters[3], c.parameters[4]
          @dests[id] ||= [x, y, (d.to_i == 0 ? 2 : d)]
        end
      end
      infos.keys.each do |id|
        m = (load_data(format('Data/Map%03d.%s', id, ext)) rescue nil)
        next unless m
        m.events.each_value { |e| e.pages.each { |pg| scan.call(pg.list) } }
      end
      (load_data("Data/CommonEvents.#{ext}") rescue []).compact.each { |ce| scan.call(ce.list) }
      start = ($data_system.start_map_id rescue nil) || ($game_map.map_id rescue 0)
      ids = ([start] + @dests.keys).uniq.select { |id| infos.key?(id) }
      ids = infos.keys.sort if ids.size <= 1
      # 문제 조사용: RR_AUTOTEST_MAPS="58,60" 이면 그 맵만
      only = ENV['RR_AUTOTEST_MAPS'].to_s.split(',').map(&:to_i).reject(&:zero?)
      ids = only unless only.empty?
      ids.sort.first(@max_maps).map { |id| [id, (infos[id].name.to_s rescue '')] }
    rescue StandardError => e
      log("MAPINFOS #{e.class}: #{e.message}")
      []
    end

    # 장소 이동 명령의 도착 좌표, 없으면 이벤트 바로 아래(위를 보도록) — 확인 키가 그 이벤트와 상호작용하게.
    def spot_for(id)
      return @dests[id] if @dests && @dests[id]
      m = load_data(format('Data/Map%03d.%s', id, ext))
      evs = m.events.values.sort_by(&:id).select { |e| e.y + 1 < m.height }
      ev = evs.empty? ? nil : evs[(@index * 5) % evs.size]
      return [ev.x, ev.y + 1, 8] if ev
      [m.width / 2, m.height / 2, 2]
    rescue StandardError
      [0, 0, 2]
    end

    def transfer(id)
      x, y, d = spot_for(id)
      case @rgss
      when 1
        $game_temp.player_transferring = true
        $game_temp.player_new_map_id = id
        $game_temp.player_new_x = x
        $game_temp.player_new_y = y
        $game_temp.player_new_direction = d
      else
        $game_player.reserve_transfer(id, x, y, d)
      end
      @target = id
    rescue StandardError => e
      log("TRANSFER #{id} #{e.class}: #{e.message}")
      @target = id
    end

    def back_to_map
      if @rgss >= 3
        SceneManager.goto(Scene_Map)
      else
        $scene = Scene_Map.new
      end
    rescue StandardError
    end

    # 타이틀이 확인 키에 반응하지 않는 게임: 게임 스크립트로 새 게임을 시작합니다.
    def force_new_game
      @forced_new = true
      case @rgss
      when 3
        DataManager.setup_new_game
        $game_map.autoplay
        SceneManager.goto(Scene_Map)
      else
        s = scene
        if s && defined?(Scene_Title) && s.is_a?(Scene_Title) && s.respond_to?(:command_new_game, true)
          s.send(:command_new_game)
        else
          $data_system ||= load_data("Data/System.#{ext}")
          t = Scene_Title.allocate
          if t.respond_to?(:create_game_objects, true)
            t.send(:create_game_objects)
          else
            $game_temp = Game_Temp.new; $game_system = Game_System.new; $game_switches = Game_Switches.new
            $game_variables = Game_Variables.new; $game_self_switches = Game_SelfSwitches.new
            $game_screen = Game_Screen.new; $game_actors = Game_Actors.new; $game_party = Game_Party.new
            $game_troop = Game_Troop.new; $game_map = Game_Map.new; $game_player = Game_Player.new
          end
          $game_party.setup_starting_members
          $game_map.setup($data_system.start_map_id)
          $game_player.moveto($data_system.start_x, $data_system.start_y)
          $game_player.refresh
          $game_map.autoplay
          $game_map.update
          $scene = Scene_Map.new
        end
      end
      log("FORCED new game from scene=#{scene_name}")
    rescue StandardError => e
      log("FORCE NEW GAME failed #{e.class}: #{e.message}")
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
        gm && (gm.choice? || gm.num_input?)
      end
    rescue StandardError
      false
    end

    def c_key?(key)
      key == Input::C || key == :C
    end

    # 확인 키: 대화/선택지/타이틀을 넘기고, 맵에서는 앞의 이벤트와 상호작용합니다.
    # 선택지에서 아래 키를 더 눌러야 하면 다 누를 때까지 확인하지 않습니다.
    def confirm?(key)
      return false if @done || !c_key?(key) || !(@frame % 12).zero?
      return @ex_press ? true : false if @phase == :extra
      # 맵 시간이 끝난 뒤엔 열린 대화를 닫을 때만 (아니면 앞 이벤트와의 대화가 끝없이 다시 시작됨)
      return false if @phase == :maps && @map_frames >= @per_map && !message_open?
      !(@choice_key && @downs_done.to_i < @downs_needed.to_i)
    end

    # 지금 떠 있는 선택지 (숫자 입력은 제외): [식별 문자열, 항목 수] 또는 nil
    def current_choice
      case @rgss
      when 1
        t = $game_temp
        return nil unless t && t.choice_max.to_i > 0
        [t.message_text.to_s, t.choice_max.to_i]
      when 2
        gm = $game_message
        return nil unless gm && gm.choice_max.to_i > 0
        [(gm.texts || []).join("\n"), gm.choice_max.to_i]
      else
        gm = $game_message
        return nil unless gm && gm.choice?
        [(gm.texts || []).join("\n") + '|' + gm.choices.join('|'), gm.choices.size]
      end
    rescue StandardError
      nil
    end

    # 선택지는 처음엔 기본 항목을 고릅니다(이름 고르기 등에서 아래 키를 누르면 오히려 처음으로 돌아가는 게임이 있음).
    # 같은 선택지가 다시 나오면(틀린 답 → 반복) 그때마다 한 칸씩 다른 항목을 고릅니다.
    def track_choice
      c = current_choice
      if c.nil?
        @choice_key = nil
        return
      end
      return if c[0] == @choice_key
      @choice_key = c[0]
      @choice_seen ||= Hash.new(0)
      @choice_seen[c[0]] += 1
      @downs_needed = (@choice_seen[c[0]] - 1) % [c[1], 1].max
      @downs_done = 0
    end

    def down?(key)
      return false if @done || !(key == Input::DOWN || key == :DOWN)
      return false unless @choice_key && (@frame % 12) == 6 && @downs_done.to_i < @downs_needed.to_i
      @downs_done += 1
      true
    end

    # 방향 입력: 맵에서 실제 플레이처럼 걷게 합니다 (동료 따라오기·접촉 이벤트·인카운트 등 이동 관련 스크립트 검사).
    def dir4
      return 0 if @done || @phase != :maps
      w = @map_frames / 20
      return 0 if (w % 4) == 3
      [2, 4, 6, 8][(w * 7 + @index) % 4]
    end

    def tick
      @frame += 1
      return if @done
      RocketAutotestInput.ensure if @frame % 60 == 1
      Graphics.frame_rate = 120 if @frame == 1 && Graphics.frame_rate < 120 rescue nil
      name = scene_name
      if name != @last_scene
        @last_scene = name
        @scene_logs += 1
        log("SCENE #{name}") if @scene_logs <= 200
      end
      track_choice
      return extra_tick if @phase == :extra
      if @phase == :boot
        if on_map? && $game_map && $game_map.map_id.to_i > 0 && ENV['RR_EXTRA_TEST'] == '1'
          log("EXTRA begin map=#{$game_map.map_id}")
          @phase = :extra
          return
        end
        if on_map? && $game_map && $game_map.map_id.to_i > 0
          @maps = map_list
          # 무작위 인카운트는 끔 (이벤트 전투는 그대로) — 전투 반복으로 시간을 쓰거나 강제 종료 부작용이 나지 않게
          begin
            if @rgss >= 3 then $game_system.disable_encounter else $game_system.encounter_disabled = true end
          rescue StandardError
          end
          log("START scene=#{name} map=#{$game_map.map_id} maps=#{@maps.size}")
          @phase = :maps
          next_map
        elsif @frame > 3600
          log("STUCK before map scene=#{name}")
          finish
        elsif @frame == 900 && !@forced_new
          force_new_game
        elsif name == 'Scene_Name' || name == 'Scene_Save' || name == 'Scene_File'
          back_to_map
        end
        return
      end

      end_long_battle
      if on_map?
        @off_map = 0
      else
        @off_map += 1
        # 전투는 끝날 때까지 기다립니다 (강제로 빠져나오면 게임 상태가 깨져 가짜 오류가 납니다). 메뉴/저장 화면만 되돌립니다.
        if !in_battle? && @off_map > 180
          log("LEFT MAP scene=#{name} -> back")
          @off_map = 0
          back_to_map
        end
      end
      @map_frames += 1
      # 실제 플레이어는 이벤트(컷신) 도중에 순간이동할 수 없으므로 이벤트가 끝나길 기다립니다 (최대 600프레임 추가).
      # 대화가 떠 있는 동안은 절대 순간이동하지 않음 (말풍선이 가리키는 이벤트가 새 맵에 없어 가짜 오류가 났음)
      next_map if on_map? && @map_frames >= @per_map && (idle? || (@map_frames >= @per_map + 600 && !message_open?) || @map_frames >= @per_map + 2400)
    end

    # 확인 키만 누르는 테스트는 전투를 끝내지 못하거나 계속 져서 무한 반복할 수 있습니다.
    # 전투가 600프레임을 넘으면 적을 쓰러뜨려 게임의 정상 승리 처리를 타게 하고, 1500프레임이 넘으면 전투를 중단합니다.
    def end_long_battle
      unless in_battle?
        @battle_frames = 0
        @battle_aborted = false
        return
      end
      @battle_frames = (@battle_frames || 0) + 1
      # 적 HP를 0으로 만들면 사이드뷰 등 전투 스크립트가 예상하지 못한 연출 경로를 타 가짜 오류가 났습니다.
      # 도망친 것처럼 전투를 끝내는 쪽이 게임 스크립트에 가장 덜 간섭합니다.
      return if @battle_frames < 600 || @battle_aborted
      case @rgss
      when 1
        $game_temp.battle_abort = true    # Scene_Battle 자체가 안전한 시점에 처리
      when 2
        # VX는 행동 연출 도중 끝내면 전투 스크립트가 빈 대상으로 계산하다 오류가 납니다. 명령 입력 중일 때만 끝냅니다.
        s = $scene
        waiting = %i[@party_command_window @actor_command_window].any? do |iv|
          w = s.instance_variable_get(iv)
          w && w.respond_to?(:active) && w.active
        end
        return unless waiting || @battle_frames > 3000
        s.send(:battle_end, 1)
      else
        BattleManager.abort
      end
      @battle_aborted = true
      log('BATTLE too long -> abort')
    rescue StandardError => e
      log("BATTLE END #{e.class}: #{e.message}")
    end

    def message_open?
      case @rgss
      when 1 then $game_temp.message_window_showing
      when 2 then $game_message.visible
      else $game_message.busy?
      end
    rescue StandardError
      false
    end

    def idle?
      return false unless on_map?
      case @rgss
      when 1 then !$game_system.map_interpreter.running? && !$game_temp.message_window_showing
      when 2 then !$game_map.interpreter.running? && !$game_message.visible
      else !$game_map.interpreter.running? && !$game_message.busy?
      end
    rescue StandardError
      true
    end

    def next_map
      if @target
        now = ($game_map.map_id rescue 0)
        log("MAP #{@target} #{now == @target ? 'ok' : "not reached (now #{now})"} scene=#{scene_name}")
      end
      @index += 1
      @map_frames = 0
      if @index >= @maps.size
        finish
        return
      end
      id, n = @maps[@index]
      log("GO #{id} #{n}")
      transfer(id)
    end

    # ---- 멀티 엑스트라 모드 시험 (RR_EXTRA_TEST=1): 첫 맵에서 가짜 참가자 캐릭터로 생성·이동·화면 제한·말 걸기·저장을 확인 ----
    # 파이프가 없어 에이전트는 매 프레임 처리를 하지 않으므로 RocketExtra.update를 여기서 부릅니다.
    def extra_tick
      @et = (@et || 0) + 1
      et = @et
      unless defined?(RocketExtra)
        log('EXTRA FAIL agent not loaded')
        return finish
      end
      if et == 1
        begin
          Marshal.dump([$game_system, $game_map, $game_player, $game_party, $game_switches, $game_variables])
          @ex_base_save = true
        rescue StandardError => e
          log("EXTRA save baseline FAIL before extra mode (game itself): #{e.class}: #{e.message}")
        end
        RocketExtra.mode(true)
        RocketExtra.set_guests("t1\x01test guest\x01#ffd34d")
        @ex_viol = 0
      end
      RocketExtra.update
      g = RocketExtra.guests['t1']
      c = g && g.char
      p = $game_player
      if et > 3 && c && !c.moving? && !RocketExtra.in_view?(c.x, c.y) && et != @ex_off
        @ex_viol += 1
      end
      # 움직일 수 있을 때까지 기다림 (확인 키로 메시지를 넘김)
      if et == 10 && !RocketExtra.can_act? && (@ex_wait = (@ex_wait || 0) + 1) < 2400
        @ex_press = true
        @et = 9
        return
      end
      if et == 10
        @ex_press = false
        log("EXTRA waited #{@ex_wait} frames for the event to end") if @ex_wait
        log("EXTRA spawn #{c && g.sprite && !g.sprite.disposed? && c.x == p.x && c.y == p.y ? 'ok' : 'FAIL'} at #{c ? "#{c.x},#{c.y}" : '-'} player #{p.x},#{p.y} image #{c ? c.character_name : '-'}")
        @ex_start = c ? [c.x, c.y] : [0, 0]
        log("EXTRA view gw=#{Graphics.width} gh=#{Graphics.height} cam=#{RocketBridge.camera.map { |v| v.round(2) }.inspect} inview=#{c && RocketExtra.in_view?(c.x, c.y)} map=#{$game_map.width}x#{$game_map.height}")
        @ex_dirs = [2, 6, 4, 8]
        @ex_moved = nil
      end
      # 방향마다 40프레임씩 눌러 보고 움직인 방향을 찾음
      if et >= 10 && et < 170 && c
        d = @ex_dirs[(et - 10) / 40]
        vk = { 2 => 0x28, 4 => 0x25, 6 => 0x27, 8 => 0x26 }[d]
        RocketExtra.held('t1', @ex_moved ? [] : [vk]) if et % 5 == 0
        @ex_moved ||= d if [c.x, c.y] != @ex_start
      end
      if et == 170
        RocketExtra.held('t1', [])
        log("EXTRA move #{@ex_moved ? "ok dir #{@ex_moved}" : 'FAIL'} #{@ex_start.join(',')} -> #{c ? "#{c.x},#{c.y}" : '-'} canAct #{RocketExtra.can_act?}")
        unless @ex_moved
          pass = lambda do |ch, d|
            x2, y2 = RocketExtra.front(ch.x, ch.y, d)
            (ch.method(:passable?).arity == 2 ? ch.passable?(x2, y2) : ch.passable?(ch.x, ch.y, d)) rescue "err #{$!.message}"
          end
          log("EXTRA passable guest #{[2, 4, 6, 8].map { |d| pass.call(c, d) }.inspect} player #{[2, 4, 6, 8].map { |d| pass.call(p, d) }.inspect} order #{g.order.inspect} through #{c.instance_variable_get(:@through).inspect}")
        end
      end
      if et == 200 && c
        @ex_off = 201
        # 화면 밖이면서 맵 안인 자리 (맵이 화면보다 작으면 건너뜀)
        @ex_offx = (0...$game_map.width).find { |x| !RocketExtra.in_view?(x, p.y) }
        if @ex_offx then c.moveto(@ex_offx, p.y) else log('EXTRA off-screen skipped (map fits the screen)') end
      end
      log("EXTRA off-screen #{@ex_offx} -> #{c && c.x == p.x && c.y == p.y ? 'back to host ok' : "FAIL #{c ? "#{c.x},#{c.y}" : '-'}"}") if et == 203 && @ex_offx
      # 말 걸기: 결정 키 이벤트 아래 칸에 세우고 위를 보게 한 뒤 확인 키
      if et == 210 && c && !RocketExtra.can_act? && (@ex_wait2 = (@ex_wait2 || 0) + 1) < 1200
        @ex_press = true
        @et = 209
        return
      end
      if et == 210 && c
        @ex_press = false
        @ex_target = $game_map.events.values.find do |ev|
          list = ev.instance_variable_get(:@list)
          ev.trigger == 0 && RocketExtra.normal?(ev) && list && list.size > 1 &&
            RocketExtra.events_at(ev.x, ev.y + 1).empty? && ev.y + 1 < $game_map.height
        end
        if @ex_target
          p.moveto(@ex_target.x, @ex_target.y + 1)
          c.moveto(@ex_target.x, @ex_target.y + 1)
          RocketExtra.turn(c, 8)
        else
          log('EXTRA talk skipped (no talk event on this map)')
        end
      end
      if et == 213 && @ex_target
        ok = RocketExtra.action(c)
        log("EXTRA talk #{ok ? 'ok' : 'FAIL'} event #{@ex_target.id} at #{@ex_target.x},#{@ex_target.y}")
      end
      if et == 230 && !@ex_base_save
        log('EXTRA save not checked (game data cannot be dumped even without extra mode)')
      elsif et == 230
        begin
          Marshal.dump([$game_system, $game_map, $game_player, $game_party, $game_switches, $game_variables])
          log('EXTRA save clean')
        rescue StandardError => e
          log("EXTRA save FAIL #{e.class}: #{e.message}")
        end
      end
      if et == 230
        @ex_sprite = g && g.sprite
        RocketExtra.mode(false)
      end
      if et == 232
        log("EXTRA off #{@ex_sprite.nil? || @ex_sprite.disposed? ? 'sprites removed' : 'FAIL'}")
        log("EXTRA DONE violations=#{@ex_viol}")
        finish
      end
    end

    def finish
      @done = true
      log("DONE")
      exit
    end
  end
end

RocketAutotest.log("BOOT rgss=#{ENV['RR_RGSS']} ruby=#{RUBY_VERSION}")
at_exit do
  e = $!
  if e && !e.is_a?(SystemExit)
    RocketAutotest.log("CRASH #{e.class}: #{e.message}")
    (e.backtrace || []).first(8).each { |l| RocketAutotest.log("  at #{l}") }
  elsif !RocketAutotest.instance_variable_get(:@done)
    # 게임 스스로 끝냄 (게임 종료 명령/타이틀의 끝내기 등). 어디서 끝냈는지 남깁니다.
    RocketAutotest.log("GAME EXIT scene=#{RocketAutotest.scene_name} #{e ? e.class : 'normal'}")
    (caller || []).first(6).each { |l| RocketAutotest.log("  at #{l}") }
  end
end

module Graphics
  class << self
    unless method_defined?(:rr_autotest_update)
      alias_method :rr_autotest_update, :update
      def update
        begin
          RocketAutotest.tick
        rescue SystemExit
          raise
        rescue StandardError, ScriptError => e
          RocketAutotest.log("AUTOTEST #{e.class}: #{e.message}")
        end
        rr_autotest_update
      end
    end
  end
end

# 입력 가로채기. 게임이 자체 입력 모듈로 Input 메서드를 다시 정의해도 매 초 그 위에 다시 감쌉니다.
# (에이전트와 서로 번갈아 감싸 사슬이 쌓이지 않도록, 감싼 대상을 RocketInputChain에 기록 — 에이전트 참고)
unless defined?(RocketInputChain)
module RocketInputChain
  WRAPPED = {}
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

module RocketAutotestInput
  @n = 0
  @mine = {}
  HOOKS = {
    trigger?: ->(key) { RocketAutotest.confirm?(key) || RocketAutotest.down?(key) },
    repeat?: ->(key) { RocketAutotest.confirm?(key) || RocketAutotest.down?(key) },
    press?: ->(key) { RocketAutotest.confirm?(key) }
  }
  def self.wrap(name, &body)
    cur = Input.method(name) rescue return
    return if @mine[name] && RocketInputChain.contains?(cur, @mine[name])
    @n += 1
    orig = :"rr_autotest_#{name.to_s.chomp('?')}_#{@n}"
    sc = Input.singleton_class
    sc.send(:alias_method, orig, name)
    sc.send(:define_method, name) { |*a| body.call(orig, a) }
    @mine[name] = Input.method(name)
    RocketInputChain::WRAPPED[@mine[name]] = cur
  end
  def self.ensure
    HOOKS.each do |name, hook|
      wrap(name) { |orig, a| hook.call(a[0]) || Input.send(orig, *a) }
    end
    wrap(:dir4) { |orig, a| (d = RocketAutotest.dir4) != 0 ? d : Input.send(orig, *a) }
  rescue StandardError => e
    RocketAutotest.log("INPUT HOOK #{e.class}: #{e.message}")
  end
end
RocketAutotestInput.ensure
end
