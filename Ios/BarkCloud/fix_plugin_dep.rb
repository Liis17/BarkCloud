#!/usr/bin/env ruby
# Try fixing the plugin product dependency: drop the "plugin:" prefix.
require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
project = Xcodeproj::Project.open(PROJECT_PATH)
target = project.targets.find { |t| t.name == 'BarkCloud' }

target.package_product_dependencies.each do |dep|
  if dep.product_name == 'plugin:GRPCProtobufGenerator'
    dep.product_name = 'GRPCProtobufGenerator'
    puts "renamed plugin product to GRPCProtobufGenerator"
  end
end

project.save
puts 'saved.'
