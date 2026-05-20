#!/usr/bin/env ruby
# Remove the failed GRPCProtobufGenerator plugin attachment. We'll run protoc
# directly from sync_proto.sh instead (fallback per the plan).
require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
project = Xcodeproj::Project.open(PROJECT_PATH)
target = project.targets.find { |t| t.name == 'BarkCloud' }

removed = []
target.package_product_dependencies.delete_if do |dep|
  if dep.product_name == 'GRPCProtobufGenerator' || dep.product_name == 'plugin:GRPCProtobufGenerator'
    removed << dep.product_name
    true
  else
    false
  end
end
puts "removed: #{removed.inspect}"

project.save
puts 'saved.'
