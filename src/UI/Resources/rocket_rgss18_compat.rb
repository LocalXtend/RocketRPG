# RocketRPG: Ruby 1.8 behaviour that RGSS1/2 (RPG Maker XP/VX) games rely on but mkxp-z's Ruby 3 removed.
# Complements mkxp-z's ruby_classic_wrap.rb (Object#type, #id, Hash#index, TRUE/FALSE/NIL). RGSS1/2 only.

if (ENV['RR_RGSS'] || '3').to_i <= 2 && !defined?(RocketRgss18Compat)
module RocketRgss18Compat; end

class Array
  # 1.8: number of non-nil elements
  def nitems; count { |x| !x.nil? }; end unless method_defined?(:nitems)
  # 1.8.7: random element
  def choice; sample; end unless method_defined?(:choice)
  def indexes(*keys); values_at(*keys); end unless method_defined?(:indexes)
  alias_method :indices, :indexes unless method_defined?(:indices)
  # 1.8: [1, "a"].to_s == "1a" (1.9+ gives the inspect form, which shows up as "[1, \"a\"]" in game text)
  def to_s; join; end
end

class Hash
  def indexes(*keys); values_at(*keys); end unless method_defined?(:indexes)
  alias_method :indices, :indexes unless method_defined?(:indices)
end

class String
  # 1.8: String was Enumerable over lines
  alias_method :each, :each_line unless method_defined?(:each)
  def to_a; lines; end unless method_defined?(:to_a)
end

# Modified RGSS1 DLLs (e.g. RGSS104E) added RGSS2-style Font.default_shadow / Font#shadow. mkxp-z only has them in
# RGSS2+, so give RGSS1 games the properties (values are kept; text is drawn without the shadow).
class Font
  %w[shadow outline].each do |prop|
    unless respond_to?(:"default_#{prop}")
      singleton_class.send(:define_method, :"default_#{prop}") { instance_variable_get(:"@rr_default_#{prop}") || false }
      singleton_class.send(:define_method, :"default_#{prop}=") { |v| instance_variable_set(:"@rr_default_#{prop}", v) }
    end
    unless method_defined?(prop)
      define_method(prop) { instance_variable_get(:"@rr_#{prop}") || false }
      define_method(:"#{prop}=") { |v| instance_variable_set(:"@rr_#{prop}", v) }
    end
  end
end

# Ruby 1.8 strings had no encoding. mkxp-z's file errors carry the (UTF-8) path as binary, so a game's own
# `print("파일 #{$!.message} ...")` handler blows up with Encoding::CompatibilityError instead of showing the message.
class SystemCallError
  alias_method :rr_compat_message, :message
  def message
    m = rr_compat_message
    if m.encoding == Encoding::BINARY
      u = m.dup.force_encoding(Encoding::UTF_8)
      m = u if u.valid_encoding?
    end
    m
  end
end

module Kernel
  # RGSS had Win32API built in; some scripts still `require 'Win32API'`
  alias_method :rr_compat_require, :require
  def require(name)
    return true if name.to_s =~ /\Awin32api(\.rb|\.so)?\z/i
    rr_compat_require(name)
  end
  private :require
end
end
