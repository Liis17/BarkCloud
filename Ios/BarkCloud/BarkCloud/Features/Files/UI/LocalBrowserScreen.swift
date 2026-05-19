import SwiftUI

struct LocalBrowserScreen: View {
    @Environment(AppEnvironment.self) private var env
    @Environment(\.dismiss) private var dismiss
    @State private var vm: LocalBrowserViewModel?

    @State private var newFolderName: String = ""
    @State private var renameTarget: FsEntry?
    @State private var renameInput: String = ""
    @State private var showNewFolderSheet = false
    @State private var showDeleteAlert = false
    @State private var pickerMode: PickerMode?
    @State private var shareItems: [URL]?
    @State private var snackbar: String?

    let initialPath: String
    let rootLabel: String

    enum PickerMode: String, Equatable { case copy, move }

    var body: some View {
        let vm = vm ?? LocalBrowserViewModel(repo: env.localFileRepository, initialPath: initialPath, rootLabel: rootLabel)
        Group {
            if vm.state.entries.isEmpty && !vm.state.isLoading {
                emptyState
            } else {
                listContent(vm: vm)
            }
        }
        .navigationTitle(vm.state.title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbar(vm: vm) }
        .safeAreaInset(edge: .bottom) {
            if !vm.state.selectionActive {
                Button {
                    newFolderName = ""
                    showNewFolderSheet = true
                } label: {
                    Label("files_action_create_folder", systemImage: "plus")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .padding()
            }
        }
        .overlay(alignment: .top) {
            if let op = vm.state.pendingOp {
                ProgressView(value: op.progress)
                    .padding(.horizontal)
                    .padding(.top, 8)
            }
        }
        .alert(String(localized: "files_dialog_delete_title"), isPresented: $showDeleteAlert) {
            Button(String(localized: "files_dialog_cancel"), role: .cancel) {}
            Button(String(localized: "files_dialog_delete_confirm"), role: .destructive) {
                vm.deleteSelected()
            }
        } message: {
            Text(String(format: NSLocalizedString("files_dialog_delete_message", comment: ""), vm.state.selection.count))
        }
        .sheet(isPresented: $showNewFolderSheet) {
            textInputSheet(
                title: String(localized: "files_dialog_new_folder_title"),
                hint: String(localized: "files_dialog_new_folder_hint"),
                confirmLabel: String(localized: "files_dialog_create"),
                text: $newFolderName,
                onConfirm: {
                    let n = newFolderName.trimmingCharacters(in: .whitespacesAndNewlines)
                    showNewFolderSheet = false
                    if !n.isEmpty { vm.createFolder(name: n) }
                }
            )
        }
        .sheet(item: $renameTarget) { target in
            textInputSheet(
                title: String(localized: "files_dialog_rename_title"),
                hint: String(localized: "files_dialog_rename_hint"),
                confirmLabel: String(localized: "files_dialog_confirm"),
                text: $renameInput,
                onConfirm: {
                    let newName = renameInput.trimmingCharacters(in: .whitespacesAndNewlines)
                    renameTarget = nil
                    if !newName.isEmpty { vm.rename(target, newName: newName) }
                }
            )
        }
        .sheet(item: Binding(
            get: { pickerMode.map { IdMode(kind: $0) } },
            set: { pickerMode = $0?.kind }
        )) { mode in
            PickFolderDialog(
                repo: env.localFileRepository,
                rootPath: vm.state.rootPath,
                startPath: vm.state.currentPath,
                forbiddenPaths: forbidden(for: vm),
                onCancel: { pickerMode = nil },
                onConfirm: { target in
                    let kind = mode.kind
                    pickerMode = nil
                    switch kind {
                    case .copy: vm.copySelected(to: target)
                    case .move: vm.moveSelected(to: target)
                    }
                }
            )
        }
        .sheet(item: Binding(
            get: { shareItems.map { SharePayload(urls: $0) } },
            set: { shareItems = $0?.urls }
        )) { payload in
            ShareSheet(items: payload.urls)
        }
        .overlay(alignment: .bottom) {
            if let snack = snackbar {
                Text(snack)
                    .font(AppTypography.bodyMedium)
                    .padding(.horizontal, 16)
                    .padding(.vertical, 12)
                    .background(AppColors.onSurface.opacity(0.85))
                    .foregroundStyle(Color.white)
                    .clipShape(Capsule())
                    .padding(.bottom, 80)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.easeInOut(duration: 0.2), value: snackbar != nil)
        .onAppear {
            if self.vm == nil {
                self.vm = LocalBrowserViewModel(repo: env.localFileRepository, initialPath: initialPath, rootLabel: rootLabel)
            }
        }
        .onChange(of: vm.pendingEvent) { _, ev in handle(ev: ev, vm: vm) }
    }

    @ViewBuilder
    private func listContent(vm: LocalBrowserViewModel) -> some View {
        List(vm.state.entries) { entry in
            FsRowItem(
                entry: entry,
                selected: vm.state.selection.contains(entry.path),
                selectionActive: vm.state.selectionActive,
                onTap: {
                    if vm.state.selectionActive {
                        vm.toggleSelect(entry)
                    } else {
                        vm.enter(entry)
                    }
                },
                onLongPress: { vm.toggleSelect(entry) },
                onAction: { action in handleRowAction(action, entry: entry, vm: vm) }
            )
        }
        .listStyle(.plain)
    }

    @ViewBuilder
    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: "folder")
                .font(.system(size: 56))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("files_empty_folder")
                .foregroundStyle(AppColors.onSurfaceVariant)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ToolbarContentBuilder
    private func toolbar(vm: LocalBrowserViewModel) -> some ToolbarContent {
        if vm.state.selectionActive {
            ToolbarItem(placement: .topBarLeading) {
                Button { vm.clearSelection() } label: { Image(systemName: "xmark") }
                    .accessibilityLabel(String(localized: "files_action_clear_selection"))
            }
            ToolbarItem(placement: .principal) {
                Text("\(vm.state.selection.count)")
                    .font(AppTypography.titleMedium)
            }
            ToolbarItemGroup(placement: .topBarTrailing) {
                if vm.state.selection.count == 1, let only = vm.selectedEntries().first {
                    Button {
                        renameTarget = only
                        renameInput = only.name
                    } label: { Image(systemName: "pencil") }
                }
                Button { pickerMode = .copy } label: { Image(systemName: "doc.on.doc") }
                Button { pickerMode = .move } label: { Image(systemName: "folder") }
                Button(role: .destructive) { showDeleteAlert = true } label: { Image(systemName: "trash") }
            }
        } else {
            if vm.state.canGoUp {
                ToolbarItem(placement: .topBarLeading) {
                    Button { vm.goUp() } label: { Image(systemName: "chevron.left") }
                        .accessibilityLabel(String(localized: "files_back"))
                }
            }
            ToolbarItemGroup(placement: .topBarTrailing) {
                Menu {
                    ForEach(FsSort.allCases, id: \.self) { s in
                        Button { vm.setSort(s) } label: { Text(s.labelKey) }
                    }
                } label: { Image(systemName: "arrow.up.arrow.down") }
                Menu {
                    Button { vm.selectAll() } label: { Label("files_action_select_all", systemImage: "checkmark.circle") }
                    Button { vm.toggleHidden() } label: {
                        Label(vm.state.showHidden ? "files_action_hide_hidden" : "files_action_show_hidden",
                              systemImage: vm.state.showHidden ? "eye.slash" : "eye")
                    }
                } label: { Image(systemName: "ellipsis.circle") }
            }
        }
    }

    @ViewBuilder
    private func textInputSheet(title: String, hint: String, confirmLabel: String, text: Binding<String>, onConfirm: @escaping () -> Void) -> some View {
        NavigationStack {
            Form {
                TextField(hint, text: text)
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(String(localized: "files_dialog_cancel")) {
                        showNewFolderSheet = false
                        renameTarget = nil
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(confirmLabel, action: onConfirm)
                        .disabled(text.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
        .presentationDetents([.medium])
    }

    private func handleRowAction(_ action: FsRowItem.RowAction, entry: FsEntry, vm: LocalBrowserViewModel) {
        switch action {
        case .open:
            shareItems = [URL(fileURLWithPath: entry.path)]
        case .share:
            vm.shareSingle(entry)
        case .rename:
            renameTarget = entry
            renameInput = entry.name
        case .copy:
            vm.state.selection = [entry.path]
            pickerMode = .copy
        case .move:
            vm.state.selection = [entry.path]
            pickerMode = .move
        case .delete:
            vm.state.selection = [entry.path]
            showDeleteAlert = true
        }
    }

    private func forbidden(for vm: LocalBrowserViewModel) -> Set<String> {
        var out: Set<String> = [vm.state.currentPath]
        for entry in vm.selectedEntries() where entry.isDirectory {
            out.insert(entry.path)
        }
        return out
    }

    private func handle(ev: BrowserEvent?, vm: LocalBrowserViewModel) {
        guard let ev else { return }
        switch ev {
        case .toast(let msg):
            snackbar = msg
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 2_500_000_000)
                if snackbar == msg { snackbar = nil }
            }
        case .openFile(let url):
            shareItems = [url]
        case .shareFiles(let urls):
            shareItems = urls
        }
        vm.eventConsumed()
    }
}

private struct IdMode: Identifiable, Equatable {
    var id: String { kind == .copy ? "copy" : "move" }
    let kind: LocalBrowserScreen.PickerMode
}

private struct SharePayload: Identifiable, Equatable {
    var id: String { urls.map(\.path).joined(separator: "|") }
    let urls: [URL]
}
