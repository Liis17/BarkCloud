#!/usr/bin/env ruby
# Sets INFOPLIST_KEY_UIBackgroundModes and INFOPLIST_KEY_BGTaskSchedulerPermittedIdentifiers
# on the main app target so BGTaskScheduler can register `com.barkfluff.BarkCloud.upload.retry`.

require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
project = Xcodeproj::Project.open(PROJECT_PATH)
target = project.targets.find { |t| t.name == 'BarkCloud' } or abort 'BarkCloud target not found'

target.build_configurations.each do |cfg|
  cfg.build_settings['INFOPLIST_KEY_UIBackgroundModes'] = 'processing'
  cfg.build_settings['INFOPLIST_KEY_BGTaskSchedulerPermittedIdentifiers'] = 'com.barkfluff.BarkCloud.upload.retry'
  puts "#{cfg.name}: BG modes/identifiers set"
end

project.save
puts 'saved.'
