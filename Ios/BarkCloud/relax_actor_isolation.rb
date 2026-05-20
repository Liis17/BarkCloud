#!/usr/bin/env ruby
# Drop SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor: incompatible with the generated
# protobuf types (they must be nonisolated/Sendable). All our app classes that
# need MainActor isolation are explicitly annotated.
require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
project = Xcodeproj::Project.open(PROJECT_PATH)
target = project.targets.find { |t| t.name == 'BarkCloud' }

target.build_configurations.each do |cfg|
  if cfg.build_settings.delete('SWIFT_DEFAULT_ACTOR_ISOLATION')
    puts "#{cfg.name}: removed SWIFT_DEFAULT_ACTOR_ISOLATION"
  end
end

project.save
puts 'saved.'
