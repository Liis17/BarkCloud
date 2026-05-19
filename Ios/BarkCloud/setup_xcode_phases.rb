#!/usr/bin/env ruby
# Adds SwiftPM packages, GRPCProtobufGenerator build-tool plugin, and "Sync Shared Proto"
# Run-Script build phase to the BarkCloud Xcode project. Idempotent.

require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
TARGET_NAME  = 'BarkCloud'

PACKAGES = [
  {
    name: 'grpc-swift-2',
    url: 'https://github.com/grpc/grpc-swift-2',
    requirement: { kind: 'upToNextMajorVersion', minimumVersion: '2.0.0' },
    products: ['GRPCCore']
  },
  {
    name: 'grpc-swift-nio-transport',
    url: 'https://github.com/grpc/grpc-swift-nio-transport',
    requirement: { kind: 'upToNextMajorVersion', minimumVersion: '2.0.0' },
    products: ['GRPCNIOTransportHTTP2']
  },
  {
    name: 'grpc-swift-protobuf',
    url: 'https://github.com/grpc/grpc-swift-protobuf',
    requirement: { kind: 'upToNextMajorVersion', minimumVersion: '2.0.0' },
    products: ['GRPCProtobuf'],
    plugin_products: ['GRPCProtobufGenerator']
  },
  {
    name: 'swift-protobuf',
    url: 'https://github.com/apple/swift-protobuf',
    requirement: { kind: 'upToNextMajorVersion', minimumVersion: '1.28.0' },
    products: ['SwiftProtobuf']
  }
]

SCRIPT_PHASE_NAME = 'Sync Shared Proto'
SCRIPT_PHASE_SHELL = '/bin/bash'
SCRIPT_PHASE_BODY = '"${SRCROOT}/sync_proto.sh"'

project = Xcodeproj::Project.open(PROJECT_PATH)
target = project.targets.find { |t| t.name == TARGET_NAME } or abort "Target #{TARGET_NAME} not found"

# 1. Add SwiftPM package references + product dependencies.
PACKAGES.each do |pkg|
  ref = project.root_object.package_references.find { |r|
    r.respond_to?(:repositoryURL) && r.repositoryURL == pkg[:url]
  }
  unless ref
    ref = project.new(Xcodeproj::Project::Object::XCRemoteSwiftPackageReference)
    ref.repositoryURL = pkg[:url]
    ref.requirement = { 'kind' => pkg[:requirement][:kind], 'minimumVersion' => pkg[:requirement][:minimumVersion] }
    project.root_object.package_references << ref
    puts "added package #{pkg[:name]}"
  else
    puts "package #{pkg[:name]} already present"
  end

  pkg[:products].each do |prod|
    existing = target.package_product_dependencies.find { |d| d.product_name == prod }
    if existing
      puts "  product #{prod} already linked"
      next
    end
    dep = project.new(Xcodeproj::Project::Object::XCSwiftPackageProductDependency)
    dep.package = ref
    dep.product_name = prod
    target.package_product_dependencies << dep

    bf = project.new(Xcodeproj::Project::Object::PBXBuildFile)
    bf.product_ref = dep
    target.frameworks_build_phase.files << bf
    puts "  linked product #{prod}"
  end

  (pkg[:plugin_products] || []).each do |prod|
    plugin_id = "plugin:#{prod}"
    existing = target.package_product_dependencies.find { |d| d.product_name == plugin_id }
    if existing
      puts "  plugin #{prod} already linked"
      next
    end
    dep = project.new(Xcodeproj::Project::Object::XCSwiftPackageProductDependency)
    dep.package = ref
    dep.product_name = plugin_id
    target.package_product_dependencies << dep
    puts "  linked plugin #{prod}"
  end
end

# 2. Add "Sync Shared Proto" Run-Script phase BEFORE Sources phase. Idempotent.
existing_script = target.shell_script_build_phases.find { |p| p.name == SCRIPT_PHASE_NAME }
if existing_script
  puts "script phase '#{SCRIPT_PHASE_NAME}' already present"
else
  phase = target.new_shell_script_build_phase(SCRIPT_PHASE_NAME)
  phase.shell_path = SCRIPT_PHASE_SHELL
  phase.shell_script = SCRIPT_PHASE_BODY
  phase.run_only_for_deployment_postprocessing = '0'
  phase.always_out_of_date = '1' # do not skip when inputs unchanged

  # Move BEFORE the Sources phase.
  sources_idx = target.build_phases.index { |p| p.isa == 'PBXSourcesBuildPhase' }
  if sources_idx && target.build_phases.last == phase
    target.build_phases.pop
    target.build_phases.insert(sources_idx, phase)
  end
  puts "added script phase '#{SCRIPT_PHASE_NAME}' before Sources"
end

project.save
puts 'saved.'
