#!/usr/bin/env ruby
# Adds explicit FileReferences to BarkCloud Networking / Session / Generated /
# Data Cache files so the Share Extension target can use the same gRPC and
# upload stack as the main app. Idempotent.

require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
SHARE_TARGET = 'ShareExtension'

# All paths are relative to the project root.
SHARED_SOURCES = [
  'BarkCloud/Networking/AuthInterceptor.swift',
  'BarkCloud/Networking/AuthErrorCodes.swift',
  'BarkCloud/Networking/CloudErrorCodes.swift',
  'BarkCloud/Networking/Base64Header.swift',
  'BarkCloud/Networking/GrpcError.swift',
  'BarkCloud/Networking/GrpcManager.swift',
  'BarkCloud/Networking/InsecureURLSession.swift',
  'BarkCloud/Networking/FileTransferService.swift',
  'BarkCloud/Networking/XAppInterceptor.swift',
  'BarkCloud/Networking/XDeviceInterceptor.swift',
  'BarkCloud/Networking/XIpInterceptor.swift',
  'BarkCloud/Networking/XOsInterceptor.swift',
  'BarkCloud/Networking/UploadConstants.swift',
  'BarkCloud/Networking/MultipartBodyBuilder.swift',
  'BarkCloud/Networking/BackgroundUploadCoordinator.swift',
  'BarkCloud/Networking/UploadLiveActivityController.swift',
  'BarkCloud/Session/SessionStore.swift',
  'BarkCloud/Data/Cache/UploadJob.swift',
  'BarkCloud/Data/Cache/UploadQueueStore.swift',
  'BarkCloud/Generated/Proto/files_api.pb.swift',
  'BarkCloud/Generated/Proto/files_api.grpc.swift',
  'BarkCloud/Generated/Proto/identity_api.pb.swift',
  'BarkCloud/Generated/Proto/identity_api.grpc.swift',
  'BarkCloud/Generated/Proto/users_api.pb.swift',
  'BarkCloud/Generated/Proto/users_api.grpc.swift',
  'BarkCloud/Generated/Proto/shared.pb.swift',
  'BarkCloud/Generated/Proto/shared.grpc.swift'
].freeze

PACKAGE_PRODUCTS = ['GRPCCore', 'GRPCNIOTransportHTTP2', 'GRPCProtobuf', 'SwiftProtobuf'].freeze

project = Xcodeproj::Project.open(PROJECT_PATH)
share_target = project.targets.find { |t| t.name == SHARE_TARGET } or abort "#{SHARE_TARGET} not found"

# ---- 1. PBXGroup "SharedSources" container for references ----
group = project.main_group.find_subpath('SharedSources', false)
if group.nil?
  group = project.main_group.new_group('SharedSources', nil)
  group.set_source_tree('SOURCE_ROOT')
  puts "created SharedSources group"
end

# ---- 2. Add Shared/UploadActivityAttributes.swift to share target sources ----
shared_group = project.main_group.find_subpath('Shared', false)
shared_swift = shared_group&.files&.find { |f| f.path == 'UploadActivityAttributes.swift' }
if shared_swift && !share_target.source_build_phase.files_references.include?(shared_swift)
  share_target.source_build_phase.add_file_reference(shared_swift)
  puts "added Shared/UploadActivityAttributes.swift to Share Extension sources"
end

# ---- 3. Create / reuse PBXFileReference for each shared source ----
SHARED_SOURCES.each do |relative|
  ref = group.files.find { |f| f.path == relative }
  if ref.nil?
    ref = group.new_reference(relative)
    ref.source_tree = 'SOURCE_ROOT'
    puts "added FileRef #{relative}"
  end
  unless share_target.source_build_phase.files_references.include?(ref)
    share_target.source_build_phase.add_file_reference(ref)
    puts "added #{relative} to Share Extension sources"
  end
end

# ---- 4. Link SwiftPM products to Share Extension target ----
PACKAGE_PRODUCTS.each do |prod|
  existing = share_target.package_product_dependencies.find { |d| d.product_name == prod }
  if existing
    puts "#{prod} already linked to Share Extension"
    next
  end
  # Find package_reference owned by the project (same URL as main target)
  main_target = project.targets.find { |t| t.name == 'BarkCloud' }
  main_dep = main_target.package_product_dependencies.find { |d| d.product_name == prod } or next
  pkg_ref = main_dep.package

  dep = project.new(Xcodeproj::Project::Object::XCSwiftPackageProductDependency)
  dep.package = pkg_ref
  dep.product_name = prod
  share_target.package_product_dependencies << dep

  bf = project.new(Xcodeproj::Project::Object::PBXBuildFile)
  bf.product_ref = dep
  share_target.frameworks_build_phase.files << bf
  puts "linked SwiftPM #{prod} to Share Extension"
end

# ---- 5. ENABLE_USER_SCRIPT_SANDBOXING off (proto sync script touches Generated/) ----
share_target.build_configurations.each do |cfg|
  cfg.build_settings['ENABLE_USER_SCRIPT_SANDBOXING'] = 'NO'
end
puts "disabled script sandbox on Share Extension"

project.save
puts 'saved.'
