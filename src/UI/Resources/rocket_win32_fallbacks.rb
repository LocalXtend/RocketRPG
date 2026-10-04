# RocketRPG: behaviour for game-bundled 32-bit helper DLLs that 64-bit mkxp-z cannot load.
# Loaded after mkxp-z's win32_wrap.rb. win32_wrap answers every call into a missing DLL with 0, which many
# scripts read as "success" (e.g. HNRGDS video: OpenMovieA == 0 → then a 0x0 Bitmap → "failed to create bitmap").
# Here known libraries get answers that make the game take its own "not available" path, and the player is told
# once which feature is skipped.

unless defined?(RocketWin32Fallback)
module RocketWin32Fallback
  # dll (lowercase, no .dll) => { function => return value }, plus a note for the player
  TABLE = {
    'hnrgds' => { note: '동영상 재생 DLL(HNRGDS)은 네이티브 모드에서 쓸 수 없어 동영상을 건너뜁니다.',
                  ret: { 'openmoviea' => -1, 'openmoview' => -1, 'getstate' => 0 } },
    'tktk_bitmap' => { note: '이미지 효과 DLL(tktk_bitmap)은 네이티브 모드에서 쓸 수 없어 일부 화면 효과가 생략됩니다.', ret: {} },
    'mgc_map_ace' => { note: '3D 맵 DLL(MGC)은 네이티브 모드에서 쓸 수 없어 일부 맵 연출이 다르게 보일 수 있습니다.', ret: {} }
  }.freeze
  @noticed = {}

  def self.answer(dll, func)
    key = dll.to_s.downcase.sub(/\.dll\z/, '')
    entry = TABLE[key]
    note = entry ? entry[:note] : "게임 전용 DLL(#{dll})은 네이티브 모드에서 쓸 수 없어 일부 기능이 동작하지 않을 수 있습니다."
    unless @noticed[key]
      @noticed[key] = true
      begin
        RocketBridge.notice(note) if defined?(RocketBridge)
      rescue StandardError
      end
    end
    return nil unless entry
    entry[:ret][func.to_s.downcase]
  end
end

if defined?(Win32API) && Win32API.method_defined?(:mkxp_native_call)
  class Win32API
    alias_method :rr_fallback_call, :call
    def call(*args)
      if !@mkxp_native_available && !@mkxp_wrap_impl
        v = RocketWin32Fallback.answer(@dll, @func)
        return v unless v.nil?
      end
      rr_fallback_call(*args)
    end
  end
end
end
