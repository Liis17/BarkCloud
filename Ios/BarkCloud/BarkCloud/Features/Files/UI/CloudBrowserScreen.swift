import SwiftUI
import PhotosUI
import UniformTypeIdentifiers

/// Браузер облачного хранилища: навигация по папкам, операции CRUD, загрузка
/// и открытие файлов. Рекурсивно переиспользует себя для вложенных папок.
struct CloudBrowserScreen: View {
    let directoryID: String
    let title: String

    @Environment(AppEnvironment.self) private var env
    @State private var vm: CloudBrowserViewModel?

    @State private var pickerItems: [PhotosPickerItem] = []
    @State private var showDocPicker = false
    @State private var showCreateFolder = false
    @State private var folderName = ""
    @State private var renameSubject: RenameSubject?
    @State private var renameText = ""
    @State private var moveSubject: MoveSubject?
    @State private var openFile: CloudFileEntry?
    @State private var shareWithUserContext: ShareWithUserContext?
    @State private var showBatchMove = false
    @State private var showBatchDeleteConfirm = false

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbarContent }
        .task {
            if vm == nil {
                vm = CloudBrowserViewModel(directoryID: directoryID, title: title, cloud: env.cloudRepository)
            }
            await vm?.loadIfNeeded()
        }
        .onChange(of: pickerItems) { _, items in handlePhotos(items) }
        .fileImporter(isPresented: $showDocPicker, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            handleDocuments(result)
        }
        .alert(String(localized: "cloud_new_folder"), isPresented: $showCreateFolder) {
            TextField(String(localized: "cloud_folder_name"), text: $folderName)
            Button(String(localized: "action_create")) {
                Task { await vm?.createFolder(name: folderName) }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
        .alert(String(localized: "action_rename"), isPresented: Binding(
            get: { renameSubject != nil }, set: { if !$0 { renameSubject = nil } })) {
            TextField(String(localized: "cloud_new_name"), text: $renameText)
            Button(String(localized: "action_save")) { performRename() }
            Button(String(localized: "action_cancel"), role: .cancel) { renameSubject = nil }
        }
        .sheet(item: $moveSubject) { subject in
            CloudMovePicker(cloud: env.cloudRepository) { targetID in
                performMove(subject, to: targetID)
            }
        }
        .sheet(isPresented: $showBatchMove) {
            CloudMovePicker(cloud: env.cloudRepository) { targetID in
                Task { await vm?.moveSelected(toDirectory: targetID) }
            }
        }
        .safeAreaInset(edge: .bottom) {
            if let vm, vm.state.isSelecting {
                selectionBar(vm)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.spring(response: 0.35, dampingFraction: 0.85), value: vm?.state.isSelecting ?? false)
        .sheet(item: $shareWithUserContext) { context in
            ShareWithUserSheet(context: context) { shareWithUserContext = nil }
        }
        .sheet(item: Binding(
            get: { vm?.state.pendingShareURL },
            set: { vm?.state.pendingShareURL = $0 }
        )) { item in
            ActivityViewController(activityItems: [item.url])
        }
        .overlay(alignment: .bottom) { if let vm { snackbarView(vm) } }
        .fullScreenCover(item: $openFile) { entry in
            NavigationStack {
                RemoteFilePreviewScreen(fileID: entry.fileID, fileName: entry.name, transfer: env.fileTransfer, cache: env.fileCache)
                    .toolbar {
                        ToolbarItem(placement: .topBarLeading) {
                            Button(String(localized: "action_close")) { openFile = nil }
                        }
                    }
            }
        }
    }

    @ViewBuilder
    private func content(_ vm: CloudBrowserViewModel) -> some View {
        if vm.state.isLoading {
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if vm.state.isEmpty {
            ScrollView {
                VStack(spacing: 16) {
                    Image(systemName: "folder")
                        .font(.system(size: 56))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text("cloud_empty_folder")
                        .font(AppTypography.titleMedium)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .frame(maxWidth: .infinity)
                .containerRelativeFrame(.vertical)
            }
            // Потянуть вниз — перезагрузить даже пустую папку.
            .barkRefreshable { await vm.reload(showSpinner: false) }
        } else {
            List {
                ForEach(vm.state.subdirs) { dir in
                    if vm.state.isSelecting {
                        selectableRow(isSelected: vm.state.selectedDirs.contains(dir.id)) {
                            folderRow(dir)
                        }
                        .contentShape(Rectangle())
                        .onTapGesture { vm.toggleDirectory(dir) }
                    } else {
                        NavigationLink {
                            CloudBrowserScreen(directoryID: dir.id, title: dir.name)
                        } label: {
                            folderRow(dir)
                        }
                        .swipeActions(edge: .trailing) {
                            deleteButton { vm.deleteDirectory(dir) }
                            moveButton { moveSubject = .directory(dir) }
                            renameButton {
                                renameText = dir.name
                                renameSubject = .directory(dir)
                            }
                        }
                    }
                }
                ForEach(vm.state.files) { entry in
                    if vm.state.isSelecting {
                        selectableRow(isSelected: vm.state.selectedFiles.contains(entry.id)) {
                            fileRow(entry)
                        }
                        .contentShape(Rectangle())
                        .onTapGesture { vm.toggleFile(entry) }
                    } else {
                    fileRow(entry)
                        .contentShape(Rectangle())
                        .onTapGesture { openFile = entry }
                        .swipeActions(edge: .trailing) {
                            deleteButton { vm.deleteFile(entry) }
                            moveButton { moveSubject = .file(entry) }
                            renameButton {
                                renameText = entry.name
                                renameSubject = .file(entry)
                            }
                        }
                        .contextMenu {
                            Button {
                                Task { await vm.makePublic(entry) }
                            } label: {
                                Label(String(localized: "ctx_make_public"), systemImage: "link")
                            }
                            Button {
                                shareWithUserContext = ShareWithUserContext(fileID: entry.fileID, fileName: entry.name)
                            } label: {
                                Label(String(localized: "shared_with_user_action"), systemImage: "person.2")
                            }
                            Button {
                                renameText = entry.name
                                renameSubject = .file(entry)
                            } label: {
                                Label(String(localized: "action_rename"), systemImage: "pencil")
                            }
                            Button {
                                moveSubject = .file(entry)
                            } label: {
                                Label(String(localized: "action_move"), systemImage: "folder")
                            }
                            Button(role: .destructive) {
                                vm.deleteFile(entry)
                            } label: {
                                Label(String(localized: "action_delete"), systemImage: "trash")
                            }
                        }
                    }
                }
            }
            .listStyle(.plain)
            // Потянуть вниз — перезагрузить содержимое папки.
            .barkRefreshable { await vm.reload(showSpinner: false) }
            .overlay(alignment: .bottom) { PendingDeleteSnackbar(store: vm.pendingDelete) }
        }
    }

    @ViewBuilder
    private func folderRow(_ dir: CloudDirectory) -> some View {
        HStack(spacing: 14) {
            Image(systemName: "folder.fill")
                .font(.system(size: 22))
                .foregroundStyle(AppColors.accent)
                .frame(width: 36)
            Text(verbatim: dir.name)
                .font(AppTypography.bodyLarge)
            Spacer()
        }
    }

    @ViewBuilder
    private func fileRow(_ entry: CloudFileEntry) -> some View {
        HStack(spacing: 14) {
            if let preview = entry.asset.preview(preferredWidth: 128) {
                RemoteImage(fileId: entry.fileID, variant: .preview(width: preview.width), url: preview.url, contentMode: .fill) {
                    Image(systemName: MimeIcon.iconSymbol(forFileName: entry.name))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .frame(width: 36, height: 36)
                .clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                Image(systemName: MimeIcon.iconSymbol(forFileName: entry.name))
                    .font(.system(size: 22))
                    .foregroundStyle(AppColors.onSurfaceVariant)
                    .frame(width: 36)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: entry.name)
                    .font(AppTypography.bodyLarge)
                    .lineLimit(1)
                Text(verbatim: FormatUtils.formatSize(entry.asset.fileSize))
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            Spacer()
        }
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        if let vm, vm.state.isSelecting {
            ToolbarItem(placement: .topBarTrailing) {
                Button(String(localized: "action_cancel")) { vm.exitSelection() }
            }
        } else if vm?.state.isUploading == true {
            ToolbarItem(placement: .topBarTrailing) { ProgressView() }
        } else {
            ToolbarItem(placement: .topBarTrailing) {
                Button(String(localized: "gallery_select")) { vm?.enterSelection() }
                    .disabled(vm?.state.isEmpty ?? true)
            }
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button {
                        folderName = ""
                        showCreateFolder = true
                    } label: {
                        Label(String(localized: "cloud_new_folder"), systemImage: "folder.badge.plus")
                    }
                    PhotosPicker(selection: $pickerItems, maxSelectionCount: 10, matching: .any(of: [.images, .videos])) {
                        Label(String(localized: "cloud_upload_media"), systemImage: "photo")
                    }
                    Button {
                        showDocPicker = true
                    } label: {
                        Label(String(localized: "cloud_upload_document"), systemImage: "doc")
                    }
                } label: {
                    Image(systemName: "plus")
                }
            }
        }
    }

    /// Строка списка в режиме выбора: ведущая галочка + контент.
    @ViewBuilder
    private func selectableRow<Content: View>(isSelected: Bool, @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 12) {
            Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                .font(.system(size: 20))
                .foregroundStyle(isSelected ? AppColors.accent : AppColors.onSurfaceVariant)
            content()
        }
    }

    /// Нижняя панель режима выбора: счётчик + «Переместить»/«Удалить» (или прогресс батча).
    @ViewBuilder
    private func selectionBar(_ vm: CloudBrowserViewModel) -> some View {
        VStack(spacing: 8) {
            if vm.state.isProcessing {
                VStack(spacing: 6) {
                    Text(verbatim: "\(String(localized: "media_deleting")) \(vm.state.processDone)/\(vm.state.processTotal)")
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    ProgressView(value: Double(vm.state.processDone), total: Double(max(vm.state.processTotal, 1)))
                        .tint(AppColors.accent)
                }
                .transition(.opacity)
            } else {
                Text(String(format: NSLocalizedString("media_selected_count", comment: ""), vm.state.selectedCount))
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
                HStack(spacing: 12) {
                    Button {
                        showBatchMove = true
                    } label: {
                        Label("action_move", systemImage: "folder")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(AppColors.accent)

                    Button(role: .destructive) {
                        showBatchDeleteConfirm = true
                    } label: {
                        Label("action_delete", systemImage: "trash")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(AppColors.error)
                    .popover(isPresented: $showBatchDeleteConfirm, arrowEdge: .bottom) {
                        batchDeleteConfirm(vm)
                    }
                }
                .controlSize(.large)
                .disabled(!vm.state.hasSelection)
                .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .background(.bar)
        .animation(.easeInOut(duration: 0.2), value: vm.state.isProcessing)
    }

    private func batchDeleteConfirm(_ vm: CloudBrowserViewModel) -> some View {
        VStack(spacing: 14) {
            Text(String(format: NSLocalizedString("cloud_delete_selected_message", comment: ""), vm.state.selectedCount))
                .font(AppTypography.bodyMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Button(role: .destructive) {
                showBatchDeleteConfirm = false
                Task { await vm.deleteSelected() }
            } label: {
                Label("action_delete", systemImage: "trash")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(AppColors.error)
            .controlSize(.large)
        }
        .padding(16)
        .frame(width: 240)
        .presentationCompactAdaptation(.popover)
    }

    @ViewBuilder
    private func snackbarView(_ vm: CloudBrowserViewModel) -> some View {
        if let text = vm.state.snackbar {
            Text(verbatim: text)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurface)
                .padding(12)
                .background(.regularMaterial)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .padding(.bottom, 16)
                .onAppear {
                    Task { @MainActor in
                        try? await Task.sleep(nanoseconds: 2_000_000_000)
                        vm.snackbarShown()
                    }
                }
        }
    }

    private func deleteButton(_ action: @escaping () -> Void) -> some View {
        Button(role: .destructive, action: action) {
            Image(systemName: "trash")
        }
        .accessibilityLabel(String(localized: "action_delete"))
    }

    private func moveButton(_ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: "folder")
        }
        .tint(.orange)
        .accessibilityLabel(String(localized: "action_move"))
    }

    private func renameButton(_ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: "pencil")
        }
        .tint(AppColors.accent)
        .accessibilityLabel(String(localized: "action_rename"))
    }

    private func performRename() {
        guard let subject = renameSubject else { return }
        let name = renameText
        Task {
            switch subject {
            case .directory(let dir): await vm?.renameDirectory(dir, newName: name)
            case .file(let entry): await vm?.renameFile(entry, newName: name)
            }
        }
        renameSubject = nil
    }

    private func performMove(_ subject: MoveSubject, to targetID: String) {
        Task {
            switch subject {
            case .directory(let dir): await vm?.moveDirectory(dir, toDirectory: targetID)
            case .file(let entry): await vm?.moveFile(entry, toDirectory: targetID)
            }
        }
        moveSubject = nil
    }

    private func handlePhotos(_ items: [PhotosPickerItem]) {
        guard !items.isEmpty else { return }
        Task {
            var files: [(data: Data, fileName: String)] = []
            for item in items {
                if let data = try? await item.loadTransferable(type: Data.self) {
                    let ext = item.supportedContentTypes.first?.preferredFilenameExtension ?? "dat"
                    files.append((data, "\(UUID().uuidString).\(ext)"))
                }
            }
            pickerItems = []
            await vm?.upload(files)
        }
    }

    private func handleDocuments(_ result: Result<[URL], Error>) {
        guard case .success(let urls) = result, !urls.isEmpty else { return }
        Task {
            var files: [(data: Data, fileName: String)] = []
            for url in urls {
                let scoped = url.startAccessingSecurityScopedResource()
                defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                if let data = try? Data(contentsOf: url) {
                    files.append((data, url.lastPathComponent))
                }
            }
            await vm?.upload(files)
        }
    }

    private enum RenameSubject: Identifiable {
        case directory(CloudDirectory)
        case file(CloudFileEntry)
        var id: String {
            switch self {
            case .directory(let d): return "d-\(d.id)"
            case .file(let f): return "f-\(f.id)"
            }
        }
    }

    private enum MoveSubject: Identifiable {
        case directory(CloudDirectory)
        case file(CloudFileEntry)
        var id: String {
            switch self {
            case .directory(let d): return "d-\(d.id)"
            case .file(let f): return "f-\(f.id)"
            }
        }
    }
}

/// Лёгкий выбор папки-назначения для перемещения: навигация по облаку с кнопкой
/// «Переместить сюда».
private struct CloudMovePicker: View {
    let cloud: CloudRepository
    let onPick: (String) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var currentID = ""
    @State private var currentName = ""
    @State private var subdirs: [CloudDirectory] = []
    @State private var isLoading = true

    var body: some View {
        NavigationStack {
            List {
                ForEach(subdirs) { dir in
                    Button {
                        currentID = dir.id
                        currentName = dir.name
                    } label: {
                        HStack {
                            Image(systemName: "folder.fill").foregroundStyle(AppColors.accent)
                            Text(verbatim: dir.name)
                            Spacer()
                            Image(systemName: "chevron.right").foregroundStyle(AppColors.onSurfaceVariant)
                        }
                    }
                    .buttonStyle(.plain)
                }
                if subdirs.isEmpty && !isLoading {
                    Text("cloud_move_no_subfolders").foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
            .overlay { if isLoading { ProgressView() } }
            .navigationTitle(currentID.isEmpty ? String(localized: "cloud_root") : currentName)
            .navigationBarTitleDisplayMode(.inline)
            .task(id: currentID) { await load() }
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button(String(localized: "action_cancel")) { dismiss() }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button(String(localized: "cloud_move_here")) {
                        onPick(currentID)
                        dismiss()
                    }
                }
            }
        }
    }

    private func load() async {
        isLoading = true
        if let listing = try? await cloud.listDirectory(currentID) {
            subdirs = listing.subdirs
        }
        isLoading = false
    }
}
