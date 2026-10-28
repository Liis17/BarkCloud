using System.Text.Json;

using BarkCloud.Files.Domain;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BarkCloud.Files.Persistence;

public class FilesContext : DbContext
{

    public FilesContext(DbContextOptions<FilesContext> options) : base(options) { }

    public DbSet<UploadFile> UploadedFiles { get; set; }

    public DbSet<UploadSession> UploadSessions { get; set; }

    public DbSet<UploadSessionPart> UploadSessionParts { get; set; }

    public DbSet<StorageCutover> StorageCutovers { get; set; }
    public DbSet<StorageWriteActivity> StorageWriteActivities { get; set; }

    public DbSet<TempFile> TempFiles { get; set; }

    public DbSet<FileHash> FileHashes { get; set; }

    public DbSet<CloudDirectory> CloudDirectories { get; set; }

    public DbSet<CloudFileEntry> CloudFileEntries { get; set; }

    public DbSet<FilePreview> FilePreviews { get; set; }

    public DbSet<FilePlaceholder> FilePlaceholders { get; set; }

    public DbSet<Album> Albums { get; set; }

    public DbSet<AlbumItem> AlbumItems { get; set; }

    public DbSet<DynamicFolder> DynamicFolders { get; set; }

    public DbSet<FavoriteFile> FavoriteFiles { get; set; }

    public DbSet<ShareLink> ShareLinks { get; set; }

    public DbSet<FolderShareLink> FolderShareLinks { get; set; }

    public DbSet<AlbumShareLink> AlbumShareLinks { get; set; }

    public DbSet<MusicPlaylist> MusicPlaylists { get; set; }

    public DbSet<MusicPlaylistItem> MusicPlaylistItems { get; set; }

    public DbSet<MusicPlaylistShareLink> MusicPlaylistShareLinks { get; set; }

    public DbSet<MusicPlaylistGrant> MusicPlaylistGrants { get; set; }

    public DbSet<FileGrant> FileGrants { get; set; }

    public DbSet<DirectoryGrant> DirectoryGrants { get; set; }

    public DbSet<FileMetadata> FileMetadata { get; set; }

    public DbSet<FileActivityEvent> FileActivityEvents { get; set; }

    public DbSet<FileSearchAlias> FileSearchAliases { get; set; }

    public DbSet<FileTag> FileTags { get; set; }
    public DbSet<UserTag> UserTags { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<StorageCutover>(b =>
        {
            b.HasKey(x => x.MigrationId);
            b.HasIndex(x => new { x.SourceServiceUrl, x.SourceBucketName }).IsUnique();
        });

        modelBuilder.Entity<UploadFile>()
            .Property(file => file.StorageProfileId)
            .IsRequired();

        modelBuilder.Entity<UploadSession>(b =>
        {
            b.HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => new { x.Status, x.ExpiresAt });
            b.HasIndex(x => x.FileId).IsUnique();
            b.Property(x => x.IdempotencyKey).HasMaxLength(128);
            b.Property(x => x.FileName).HasMaxLength(512);
            b.Property(x => x.ContentType).HasMaxLength(255);
            b.Property(x => x.Sha256).HasMaxLength(64);
            b.Property(x => x.FileType).HasDefaultValue(UploadFileType.CloudFile);
            b.Property(x => x.UploadTokenHash).HasMaxLength(64);
            b.Property(x => x.ErrorCode).HasMaxLength(64);
            b.Property(x => x.ErrorMessage).HasMaxLength(1024);
            b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        });

        modelBuilder.Entity<UploadSessionPart>(b =>
        {
            b.HasKey(x => new { x.SessionId, x.PartNumber });
            b.Property(x => x.Etag).HasMaxLength(1024);
            b.HasIndex(x => new { x.SessionId, x.UpdatedAt });
            b.HasOne<UploadSession>()
                .WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TempFile>()
            .HasIndex(x => x.OriginalFileId);

        // Configure FileHashes with index on Hash for fast lookups
        modelBuilder.Entity<FileHash>()
            .HasIndex(x => x.Hash);

        modelBuilder.Entity<CloudDirectory>(b =>
        {
            b.Property(x => x.InheritVisibility).HasDefaultValue(true);
            b.Property(x => x.HideFromGallery).HasDefaultValue(false);
            b.Property(x => x.HideFromSearch).HasDefaultValue(false);
            b.Property(x => x.EffectiveHideFromGallery).HasDefaultValue(false);
            b.Property(x => x.EffectiveHideFromSearch).HasDefaultValue(false);
            b.Property(x => x.SizeVersion).HasDefaultValue(0L);
            // Уникальность имени папки в рамках одного владельца и одного родителя
            b.HasIndex(x => new { x.OwnerId, x.ParentId, x.Name }).IsUnique();
            // NULL в ParentId не участвует в прежней уникальности — корень защищаем отдельно.
            b.HasIndex(x => new { x.OwnerId, x.Name })
                .IsUnique()
                .HasFilter("\"ParentId\" IS NULL");
            // Быстрый листинг детей конкретной директории владельца
            b.HasIndex(x => new { x.OwnerId, x.ParentId });
            // У владельца одна системная папка каждого типа, независимо от имени и родителя.
            b.HasIndex(x => new { x.OwnerId, x.SystemKind })
                .IsUnique()
                .HasFilter("\"SystemKind\" <> 0");
        });

        modelBuilder.Entity<CloudFileEntry>(b =>
        {
            b.HasOne<UploadFile>().WithMany().HasForeignKey(x => x.FileId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.FileId);
            // Уникальность имени файла-записи в рамках одной директории владельца.
            // Частичный индекс: записи в корзине (IsDeleted) исключаются, чтобы не блокировать
            // повторную загрузку файла с тем же именем.
            b.HasIndex(x => new { x.OwnerId, x.DirectoryId, x.Name })
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false");
            b.HasIndex(x => new { x.OwnerId, x.DirectoryId });
            // Инвариант «одна директория на файл»: у владельца не более одной живой записи на блоб.
            b.HasIndex(x => new { x.OwnerId, x.FileId })
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false");
            // Скан фоновым воркером просроченных записей корзины.
            b.HasIndex(x => x.PurgeAt)
                .HasFilter("\"IsDeleted\" = true");
        });

        modelBuilder.Entity<FilePreview>(b =>
        {
            // У одного оригинала может быть максимум одно превью каждой ширины.
            b.HasIndex(x => new { x.OriginalFileId, x.TargetWidth }).IsUnique();
            // Обратный поиск «к какому оригиналу относится это превью» —
            // нужен при подчистке Uploaders/дедупликации.
            b.HasIndex(x => x.PreviewFileId);
        });

        modelBuilder.Entity<FilePlaceholder>(b =>
        {
            b.HasKey(x => x.FileId);
            b.Property(x => x.Colors).IsRequired();
            b.HasOne<UploadFile>().WithOne().HasForeignKey<FilePlaceholder>(x => x.FileId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<FilePreview>().WithMany().HasForeignKey(x => x.SourceFilePreviewId)
                .OnDelete(DeleteBehavior.Cascade);
            b.ToTable("FilePlaceholders", table => table.HasCheckConstraint(
                "CK_FilePlaceholders_ColorCount",
                Database.IsNpgsql() ? "cardinality(\"Colors\") = 9" : "json_array_length(\"Colors\") = 9"));
        });

        modelBuilder.Entity<Album>(b =>
        {
            // Уникальность имени альбома в рамках владельца.
            b.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
            // Cursor-пагинация списка альбомов по дате обновления.
            b.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
        });

        modelBuilder.Entity<AlbumItem>(b =>
        {
            // Один файл — максимум один раз в альбоме.
            b.HasIndex(x => new { x.AlbumId, x.FileId }).IsUnique();
            // Cursor-пагинация содержимого альбома.
            b.HasIndex(x => new { x.AlbumId, x.AddedAt });
            // Обратный поиск «в каких альбомах файл».
            b.HasIndex(x => x.FileId);
        });

        modelBuilder.Entity<DynamicFolder>(b =>
        {
            // Уникальность имени умной папки в рамках владельца.
            b.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
            // Порядок ленты у владельца.
            b.HasIndex(x => new { x.OwnerId, x.SortOrder });

            // Критерии храним как единый jsonb-документ. В SQL внутрь не запрашиваем —
            // десериализуем POCO и строим запрос файлов в коде (DynamicFolderQueryBuilder).
            var jsonOptions = new JsonSerializerOptions();
            var converter = new ValueConverter<DynamicFolderCriteria, string>(
                v => JsonSerializer.Serialize(v, jsonOptions),
                v => JsonSerializer.Deserialize<DynamicFolderCriteria>(v, jsonOptions) ?? new DynamicFolderCriteria());
            var comparer = new ValueComparer<DynamicFolderCriteria>(
                (a, c) => JsonSerializer.Serialize(a, jsonOptions) == JsonSerializer.Serialize(c, jsonOptions),
                v => v == null ? 0 : JsonSerializer.Serialize(v, jsonOptions).GetHashCode(),
                v => JsonSerializer.Deserialize<DynamicFolderCriteria>(JsonSerializer.Serialize(v, jsonOptions), jsonOptions)!);

            b.Property(x => x.Criteria)
                .HasConversion(converter, comparer)
                .HasColumnType("jsonb");
        });

        modelBuilder.Entity<FavoriteFile>(b =>
        {
            // Один файл — максимум один раз в избранном пользователя (идемпотентность + защита от дублей).
            b.HasIndex(x => new { x.OwnerId, x.FileId }).IsUnique();
            // Cursor-пагинация списка избранного по дате добавления.
            b.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        });

        modelBuilder.Entity<ShareLink>(b =>
        {
            // Резолв публичной ссылки по токену — токен уникален.
            b.HasIndex(x => x.Token).IsUnique();
            // Cursor-пагинация списка ссылок владельца по дате создания.
            b.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        });

        modelBuilder.Entity<FolderShareLink>(b =>
        {
            // Резолв публичной папки по токену — токен уникален.
            b.HasIndex(x => x.Token).IsUnique();
            // Один публичный шар на папку владельца (идемпотентность CreateFolderShare).
            b.HasIndex(x => new { x.OwnerId, x.DirectoryId }).IsUnique();
            // Cursor-пагинация списка публичных папок владельца по дате создания.
            b.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        });

        modelBuilder.Entity<AlbumShareLink>(b =>
        {
            // Резолв публичного альбома по токену — токен уникален.
            b.HasIndex(x => x.Token).IsUnique();
            // Один публичный шар на альбом владельца (идемпотентность CreateAlbumShare).
            b.HasIndex(x => new { x.OwnerId, x.AlbumId }).IsUnique();
            // Cursor-пагинация списка публичных альбомов владельца по дате создания.
            b.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        });

        modelBuilder.Entity<MusicPlaylist>(b =>
        {
            b.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
        });

        modelBuilder.Entity<MusicPlaylistItem>(b =>
        {
            b.HasIndex(x => new { x.PlaylistId, x.FileId }).IsUnique();
            b.HasIndex(x => new { x.PlaylistId, x.Position });
            b.HasIndex(x => x.FileId);
        });

        modelBuilder.Entity<MusicPlaylistShareLink>(b =>
        {
            b.HasIndex(x => x.Token).IsUnique();
            b.HasIndex(x => new { x.OwnerId, x.PlaylistId }).IsUnique();
            b.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        });

        modelBuilder.Entity<MusicPlaylistGrant>(b =>
        {
            b.HasIndex(x => new { x.OwnerId, x.PlaylistId, x.RecipientId }).IsUnique();
            b.HasIndex(x => new { x.RecipientId, x.CreatedAt });
            b.HasIndex(x => x.PlaylistId);
        });

        modelBuilder.Entity<FileGrant>(b =>
        {
            // Идемпотентность: один файл — максимум один грант владельца конкретному получателю.
            b.HasIndex(x => new { x.OwnerId, x.FileId, x.RecipientId }).IsUnique();
            // Cursor-пагинация раздела «мне доступны» получателя.
            b.HasIndex(x => new { x.RecipientId, x.CreatedAt });
            // Обратный поиск/чистка по файлу.
            b.HasIndex(x => x.FileId);
        });

        modelBuilder.Entity<DirectoryGrant>(b =>
        {
            // Идемпотентность: одна папка — максимум один грант владельца конкретному получателю.
            b.HasIndex(x => new { x.OwnerId, x.DirectoryId, x.RecipientId }).IsUnique();
            // Раздел «мне доступны» (папки) получателя.
            b.HasIndex(x => new { x.RecipientId, x.CreatedAt });
            // Обратный поиск/чистка по папке.
            b.HasIndex(x => x.DirectoryId);
        });

        modelBuilder.Entity<FileActivityEvent>(b =>
        {
            // История конкретного файла в модалке свойств: свежие события первыми.
            b.HasIndex(x => new { x.OwnerId, x.FileId, x.CreatedAt });
            // Задел под будущий общий activity feed владельца.
            b.HasIndex(x => new { x.OwnerId, x.CreatedAt });
            b.Property(x => x.Kind).HasMaxLength(64);
            b.Property(x => x.Summary).HasMaxLength(512);
            b.Property(x => x.DetailsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<FileSearchAlias>(b =>
        {
            b.HasKey(x => new { x.OwnerId, x.FileId });
            b.Property(x => x.Value).HasMaxLength(120);
            b.Property(x => x.NormalizedValue).HasMaxLength(120);
            b.HasIndex(x => new { x.OwnerId, x.NormalizedValue });
            b.HasOne<UploadFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserTag>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.OwnerId, x.NormalizedName }).IsUnique();
            b.Property(x => x.Name).HasMaxLength(50);
            b.Property(x => x.NormalizedName).HasMaxLength(50);
            b.Property(x => x.Color).HasMaxLength(7);
        });

        modelBuilder.Entity<FileTag>(b =>
        {
            b.HasKey(x => new { x.OwnerId, x.FileId, x.NormalizedValue });
            b.Property(x => x.Value).HasMaxLength(50);
            b.Property(x => x.NormalizedValue).HasMaxLength(50);
            b.HasIndex(x => new { x.OwnerId, x.NormalizedValue });
            b.HasOne<UploadFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Синхронное сохранение не поддерживается: инвалидация кеша размеров асинхронная, и пропустить её молча нельзя.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Используйте SaveChangesAsync: он инвалидирует кеш размеров папок.");

    /// <summary>
    /// Сохраняет изменения и в той же транзакции инвалидирует кеш размера затронутых папок и всех их предков.
    /// Затрагивают размер: появление, удаление и перенос живых записей, перенос и удаление папок, изменение
    /// размера файла. Переименование и видимость размер не меняют.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var touched = await CollectTouchedDirectoryIdsAsync(cancellationToken);
        if (touched.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        await using var transaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        // Цепочку предков берём до и после сохранения: после переноса или удаления папки старая цепочка
        // уже не находится в БД, а новая — до сохранения.
        var affected = await CollectWithAncestorsAsync(touched, cancellationToken);
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        affected.UnionWith(await CollectWithAncestorsAsync(touched, cancellationToken));

        await CloudDirectories
            .Where(x => affected.Contains(x.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SubtreeSizeBytes, (long?)null)
                .SetProperty(x => x.SizeVersion, x => x.SizeVersion + 1), cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<HashSet<Guid>> CollectTouchedDirectoryIdsAsync(CancellationToken cancellationToken)
    {
        var touched = new HashSet<Guid>();

        foreach (var entry in ChangeTracker.Entries<CloudFileEntry>())
        {
            var affectsSize = entry.State is EntityState.Added or EntityState.Deleted
                || entry.Property(x => x.IsDeleted).IsModified
                || entry.Property(x => x.DirectoryId).IsModified;
            if (!affectsSize)
                continue;

            if (entry.State != EntityState.Added && !entry.OriginalValues.GetValue<bool>(nameof(CloudFileEntry.IsDeleted)))
                AddDirectory(touched, entry.OriginalValues.GetValue<Guid>(nameof(CloudFileEntry.DirectoryId)));
            if (entry.State != EntityState.Deleted && !entry.Entity.IsDeleted)
                AddDirectory(touched, entry.Entity.DirectoryId);
        }

        foreach (var entry in ChangeTracker.Entries<CloudDirectory>())
        {
            if (entry.State == EntityState.Deleted)
            {
                AddDirectory(touched, entry.OriginalValues.GetValue<Guid?>(nameof(CloudDirectory.ParentId)));
            }
            else if (entry.State == EntityState.Modified && entry.Property(x => x.ParentId).IsModified)
            {
                AddDirectory(touched, entry.OriginalValues.GetValue<Guid?>(nameof(CloudDirectory.ParentId)));
                AddDirectory(touched, entry.Entity.ParentId);
            }
        }

        // Размер файла (например, конвертация HEIC→JPEG) влияет на все папки с его живыми записями.
        // Update() помечает Size изменённым всегда, поэтому сравниваем с сохранённым значением.
        var sizeCandidates = ChangeTracker.Entries<UploadFile>()
            .Where(entry => entry.State == EntityState.Modified && entry.Property(x => x.Size).IsModified)
            .Select(entry => (entry.Entity.Id, entry.Entity.Size))
            .ToList();
        if (sizeCandidates.Count > 0)
        {
            var candidateIds = sizeCandidates.Select(x => x.Id).ToList();
            var storedSizes = await UploadedFiles
                .AsNoTracking()
                .Where(x => candidateIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Size })
                .ToDictionaryAsync(x => x.Id, x => x.Size, cancellationToken);
            var resizedFileIds = sizeCandidates
                .Where(x => !storedSizes.TryGetValue(x.Id, out var stored) || stored != x.Size)
                .Select(x => x.Id)
                .ToList();
            if (resizedFileIds.Count > 0)
            {
                var directoryIds = await CloudFileEntries
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted && resizedFileIds.Contains(x.FileId))
                    .Select(x => x.DirectoryId)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                foreach (var directoryId in directoryIds)
                    AddDirectory(touched, directoryId);
            }
        }

        return touched;
    }

    private async Task<HashSet<Guid>> CollectWithAncestorsAsync(IEnumerable<Guid> start, CancellationToken cancellationToken)
    {
        var result = new HashSet<Guid>(start);
        var frontier = result.ToList();
        while (frontier.Count > 0)
        {
            var parents = await CloudDirectories
                .AsNoTracking()
                .Where(x => frontier.Contains(x.Id) && x.ParentId != null)
                .Select(x => x.ParentId!.Value)
                .ToListAsync(cancellationToken);
            frontier = parents.Where(result.Add).ToList();
        }
        return result;
    }

    private static void AddDirectory(HashSet<Guid> touched, Guid? directoryId)
    {
        if (directoryId is Guid id && id != CloudHierarchyStorage.RootDirectoryId)
            touched.Add(id);
    }
}
