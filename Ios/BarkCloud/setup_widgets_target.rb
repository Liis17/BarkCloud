#!/usr/bin/env ruby
# Adds a WidgetKit / Live Activity extension target `BarkCloudWidgets` to the
# BarkCloud Xcode project, wires up the shared `Shared/` folder (with
# UploadActivityAttributes.swift accessible to both main app and the widget
# target), and turns on `NSSupportsLiveActivities` in the main app's
# generated Info.plist. Idempotent — running twice is a no-op.

require 'xcodeproj'

PROJECT_PATH = File.expand_path('BarkCloud.xcodeproj', __dir__)
MAIN_TARGET_NAME    = 'BarkCloud'
WIDGET_TARGET_NAME  = 'BarkCloudWidgets'
WIDGET_FOLDER       = 'BarkCloudWidgets'
SHARED_GROUP_NAME   = 'Shared'
SHARED_FOLDER_PATH  = 'Shared'
SHARED_FILES        = ['UploadActivityAttributes.swift'].freeze
WIDGET_BUNDLE_ID    = 'com.barkfluff.BarkCloud.Widgets'

project = Xcodeproj::Project.open(PROJECT_PATH)
main_target = project.targets.find { |t| t.name == MAIN_TARGET_NAME } or abort "Main target #{MAIN_TARGET_NAME} not found"

# ---- 1. Shared/ group (plain PBXGroup so we can add the file to multiple targets) ----

shared_group = project.main_group.find_subpath(SHARED_GROUP_NAME, false)
if shared_group.nil?
  shared_group = project.main_group.new_group(SHARED_GROUP_NAME, SHARED_FOLDER_PATH)
  puts "created Shared group"
else
  puts "Shared group already present"
end

# Make sure the group path points to the right disk folder.
shared_group.set_path(SHARED_FOLDER_PATH)
shared_group.set_source_tree('<group>')

shared_file_refs = SHARED_FILES.map do |name|
  ref = shared_group.files.find { |f| f.path == name }
  if ref.nil?
    ref = shared_group.new_reference(name)
    puts "added Shared/#{name}"
  else
    puts "Shared/#{name} already referenced"
  end
  ref
end

# Ensure Shared files compile as part of main app.
shared_file_refs.each do |ref|
  unless main_target.source_build_phase.files_references.include?(ref)
    main_target.source_build_phase.add_file_reference(ref)
    puts "added Shared/#{ref.path} to main target sources"
  end
end

# ---- 2. BarkCloudWidgets PBXFileSystemSynchronizedRootGroup ----

widgets_fs_group = project.main_group.children.find do |child|
  child.is_a?(Xcodeproj::Project::Object::PBXFileSystemSynchronizedRootGroup) &&
    child.path == WIDGET_FOLDER
end
if widgets_fs_group.nil?
  widgets_fs_group = project.new(Xcodeproj::Project::Object::PBXFileSystemSynchronizedRootGroup)
  widgets_fs_group.path = WIDGET_FOLDER
  widgets_fs_group.source_tree = '<group>'
  project.main_group.children << widgets_fs_group
  puts "created BarkCloudWidgets filesystem-synchronized group"
else
  puts "BarkCloudWidgets fs-group already present"
end

# ---- 3. BarkCloudWidgets native target ----

widget_target = project.targets.find { |t| t.name == WIDGET_TARGET_NAME }
created_target = false
if widget_target.nil?
  widget_target = project.new(Xcodeproj::Project::Object::PBXNativeTarget)
  widget_target.name = WIDGET_TARGET_NAME
  widget_target.product_name = WIDGET_TARGET_NAME
  widget_target.product_type = 'com.apple.product-type.app-extension'

  sources = project.new(Xcodeproj::Project::Object::PBXSourcesBuildPhase)
  frameworks = project.new(Xcodeproj::Project::Object::PBXFrameworksBuildPhase)
  resources = project.new(Xcodeproj::Project::Object::PBXResourcesBuildPhase)
  widget_target.build_phases << sources
  widget_target.build_phases << frameworks
  widget_target.build_phases << resources

  product_ref = project.new(Xcodeproj::Project::Object::PBXFileReference)
  product_ref.explicit_file_type = 'wrapper.app-extension'
  product_ref.include_in_index = '0'
  product_ref.path = "#{WIDGET_TARGET_NAME}.appex"
  product_ref.source_tree = 'BUILT_PRODUCTS_DIR'
  widget_target.product_reference = product_ref
  project.products_group.children << product_ref

  project.targets << widget_target
  created_target = true
  puts "created widget target"
else
  puts "widget target already present"
end

# ---- 4. Attach FS group + exception for Info.plist ----

unless widget_target.file_system_synchronized_groups.to_a.include?(widgets_fs_group)
  widget_target.file_system_synchronized_groups ||= []
  widget_target.file_system_synchronized_groups << widgets_fs_group
  puts "attached BarkCloudWidgets fs-group to widget target"
end

# Info.plist должен быть исключён из source compilation (он указан как INFOPLIST_FILE).
existing_exception = widgets_fs_group.exceptions.to_a.find do |e|
  e.is_a?(Xcodeproj::Project::Object::PBXFileSystemSynchronizedBuildFileExceptionSet) &&
    e.target == widget_target
end
if existing_exception.nil?
  exc = project.new(Xcodeproj::Project::Object::PBXFileSystemSynchronizedBuildFileExceptionSet)
  exc.target = widget_target
  exc.membership_exceptions = ['Info.plist']
  widgets_fs_group.exceptions ||= []
  widgets_fs_group.exceptions << exc
  puts "added Info.plist exception for widget target"
end

# Add Shared/UploadActivityAttributes.swift to widget sources.
shared_file_refs.each do |ref|
  unless widget_target.source_build_phase.files_references.include?(ref)
    widget_target.source_build_phase.add_file_reference(ref)
    puts "added Shared/#{ref.path} to widget target sources"
  end
end

# ---- 5. Configurations (Debug/Release) ----

if widget_target.build_configuration_list.nil?
  config_list = project.new(Xcodeproj::Project::Object::XCConfigurationList)
  config_list.default_configuration_is_visible = '0'
  config_list.default_configuration_name = 'Release'
  widget_target.build_configuration_list = config_list
end

base_settings = {
  'CODE_SIGN_ENTITLEMENTS' => "#{WIDGET_FOLDER}/#{WIDGET_TARGET_NAME}.entitlements",
  'CODE_SIGN_STYLE' => 'Automatic',
  'CURRENT_PROJECT_VERSION' => '1',
  'DEVELOPMENT_TEAM' => '9Y935NYUP9',
  'GENERATE_INFOPLIST_FILE' => 'NO',
  'INFOPLIST_FILE' => "#{WIDGET_FOLDER}/Info.plist",
  'IPHONEOS_DEPLOYMENT_TARGET' => '18.0',
  'LD_RUNPATH_SEARCH_PATHS' => ['@executable_path/Frameworks', '@executable_path/../../Frameworks'],
  'MARKETING_VERSION' => '1.0',
  'PRODUCT_BUNDLE_IDENTIFIER' => WIDGET_BUNDLE_ID,
  'PRODUCT_NAME' => '$(TARGET_NAME)',
  'REGISTER_APP_GROUPS' => 'YES',
  'SDKROOT' => 'iphoneos',
  'SKIP_INSTALL' => 'YES',
  'SUPPORTED_PLATFORMS' => 'iphoneos iphonesimulator',
  'SWIFT_APPROACHABLE_CONCURRENCY' => 'YES',
  'SWIFT_EMIT_LOC_STRINGS' => 'YES',
  'SWIFT_VERSION' => '5.0',
  'TARGETED_DEVICE_FAMILY' => '1,2'
}

['Debug', 'Release'].each do |name|
  cfg = widget_target.build_configuration_list.build_configurations.find { |c| c.name == name }
  if cfg.nil?
    cfg = project.new(Xcodeproj::Project::Object::XCBuildConfiguration)
    cfg.name = name
    widget_target.build_configuration_list.build_configurations << cfg
  end
  base_settings.each { |k, v| cfg.build_settings[k] = v }
end

# ---- 6. Target dependency: main target depends on widget target ----

existing_dep = main_target.dependencies.find do |d|
  d.target == widget_target || (d.target_proxy && d.target_proxy.remote_global_id_string == widget_target.uuid)
end
unless existing_dep
  container_proxy = project.new(Xcodeproj::Project::Object::PBXContainerItemProxy)
  container_proxy.container_portal = project.root_object.uuid
  container_proxy.proxy_type = '1'
  container_proxy.remote_global_id_string = widget_target.uuid
  container_proxy.remote_info = WIDGET_TARGET_NAME

  dep = project.new(Xcodeproj::Project::Object::PBXTargetDependency)
  dep.target = widget_target
  dep.target_proxy = container_proxy
  main_target.dependencies << dep
  puts "linked widget as dependency of main target"
end

# ---- 7. Embed Foundation Extensions: copy widget.appex into main app bundle ----

embed_phase = main_target.copy_files_build_phases.find { |p| p.name == 'Embed Foundation Extensions' }
if embed_phase.nil?
  embed_phase = main_target.new_copy_files_build_phase('Embed Foundation Extensions')
  embed_phase.symbol_dst_subfolder_spec = :plug_ins
  puts "created Embed Foundation Extensions phase"
end

embed_already = embed_phase.files.any? { |f| f.file_ref == widget_target.product_reference }
unless embed_already
  bf = project.new(Xcodeproj::Project::Object::PBXBuildFile)
  bf.file_ref = widget_target.product_reference
  bf.settings = { 'ATTRIBUTES' => ['RemoveHeadersOnCopy'] }
  embed_phase.files << bf
  puts "embedded widget.appex"
end

# ---- 8. NSSupportsLiveActivities in main app's generated Info.plist ----

main_target.build_configurations.each do |cfg|
  cfg.build_settings['INFOPLIST_KEY_NSSupportsLiveActivities'] = 'YES'
  cfg.build_settings['INFOPLIST_KEY_NSSupportsLiveActivitiesFrequentUpdates'] = 'YES'
end
puts "set NSSupportsLiveActivities=YES on main target"

project.save
puts 'saved.'
