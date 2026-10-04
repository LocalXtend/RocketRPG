# RocketRPG RGSS1/2 script runner (loaded as the LAST preload script, RGSS1/2 only).
#
# mkxp-z evaluates RGSS1 scripts with `self == nil`, like the original player. Under Ruby 1.8 that was harmless,
# but in mkxp-z's Ruby 3 `nil` is frozen, so any script that sets an instance variable at the top level
# (e.g. `@sg_temp = nil`) dies with "can't modify frozen NilClass". Ruby 1.8-only syntax (`when x:`) also fails.
#
# So we run the game's scripts ourselves, from $RGSS_SCRIPTS (already decoded by mkxp-z):
#   - each script gets a fresh top-level scope (no leaking locals), self is `main`, `def` defines global methods
#   - a script that does not parse is retried once with Ruby 1.8 syntax rewritten to modern syntax
#   - F12 reset works the same way as mkxp-z (Audio/Graphics.__reset__ then run all scripts again)
# When the game ends we exit, so mkxp-z does not run the scripts a second time.

if (ENV['RR_RGSS'] || '3').to_i <= 2 && $RGSS_SCRIPTS.is_a?(Array) && !defined?(RocketScriptLoader)
module RocketScriptLoader
  RESET = Object.const_defined?(:Reset) ? ::Reset : nil

  KEYWORDS = %w[if elsif unless while until and or not when return case then do in].freeze

  # Ruby 1.8 syntax that newer Ruby rejects (only applied to scripts that fail to parse):
  #   `when 1: foo` / `when :a, :b:`   → `when 1 then foo`
  #   `obj.valid? (x, y)` (space before a multi-argument list) → `obj.valid?(x, y)`
  def self.fix18(code)
    code = code.gsub(/^([ \t]*when\b[^\n#]*?[^:\s?]):(?=[ \t]|\r?$)/) { "#{$1} then" }
    code.gsub(/([A-Za-z_]\w*[?!]?)[ \t]+\((?=[^()\n]*,[^()\n]*\))/) do
      KEYWORDS.include?($1) ? $& : "#{$1}("
    end
  end

  def self.parses?(code, name)
    RubyVM::InstructionSequence.compile(code, name, name, 1)
    true
  rescue SyntaxError
    false
  end

  def self.run_all
    $RGSS_SCRIPTS.each_with_index do |s, i|
      next unless s.is_a?(Array)
      code = s[3].to_s
      name = s[1].to_s.dup.force_encoding('UTF-8')
      name = format('Section%03d', i) unless name.valid_encoding?
      unless parses?(code, name)
        alt = fix18(code)
        code = alt if alt != code && parses?(alt, name)
      end
      eval(code, TOPLEVEL_BINDING.dup, name, 1)
    end
  end
end

loop do
  begin
    RocketScriptLoader.run_all
    break
  rescue Exception => e
    raise unless RocketScriptLoader::RESET && e.is_a?(RocketScriptLoader::RESET)
    begin
      Audio.__reset__
      Graphics.__reset__
    rescue StandardError
    end
  end
end
exit
end
