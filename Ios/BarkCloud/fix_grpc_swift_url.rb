#!/usr/bin/env ruby
# One-off: fix the grpc-swift package URL from v1 (grpc-swift) to v2 (grpc-swift-2).
require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
project = Xcodeproj::Project.open(PROJECT_PATH)

project.root_object.package_references.each do |ref|
  if ref.respond_to?(:repositoryURL) && ref.repositoryURL == 'https://github.com/grpc/grpc-swift'
    ref.repositoryURL = 'https://github.com/grpc/grpc-swift-2'
    puts "fixed package URL → grpc-swift-2"
  end
end

project.save
puts 'saved.'
