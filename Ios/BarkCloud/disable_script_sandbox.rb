#!/usr/bin/env ruby
# Disable ENABLE_USER_SCRIPT_SANDBOXING so sync_proto.sh can read/write outside its
# declared inputs. Build phases that copy .proto from Shared/ and emit generated
# Swift files under BarkCloud/Generated/Proto/ need broad filesystem access.
require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
project = Xcodeproj::Project.open(PROJECT_PATH)
target = project.targets.find { |t| t.name == 'BarkCloud' }

target.build_configurations.each do |cfg|
  cfg.build_settings['ENABLE_USER_SCRIPT_SANDBOXING'] = 'NO'
  puts "#{cfg.name}: ENABLE_USER_SCRIPT_SANDBOXING = NO"
end

project.save
puts 'saved.'
