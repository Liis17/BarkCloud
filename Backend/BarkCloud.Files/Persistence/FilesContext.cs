using BarkCloud.Files.Domain;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Persistence;

public class FilesContext : DbContext
{

    public FilesContext(DbContextOptions<FilesContext> options) : base(options) { }

    public DbSet<UploadFile> UploadedFiles { get; set; }

    public DbSet<TempFile> TempFiles { get; set; }

    public DbSet<FileHash> FileHashes { get; set; }

    public DbSet<CloudDirectory> CloudDirectories { get; set; }

    public DbSet<CloudFileEntry> CloudFileEntries { get; set; }

    public DbSet<FilePreview> FilePreviews { get; set; }

    public DbSet<Album> Albums { get; set; }

    public DbSet<AlbumItem> AlbumItems { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TempFile>()
            .HasIndex(x => x.OriginalFileId);

        // Configure FileHashes with index on Hash for fast lookups
        modelBuilder.Entity<FileHash>()
            .HasIndex(x => x.Hash);

        modelBuilder.Entity<CloudDirectory>(b =>
        {
            // Уникальность имени папки в рамках одного владельца и одного родителя
            b.HasIndex(x => new { x.OwnerId, x.ParentId, x.Name }).IsUnique();
            // Быстрый листинг детей конкретной директории владельца
            b.HasIndex(x => new { x.OwnerId, x.ParentId });
        });

        modelBuilder.Entity<CloudFileEntry>(b =>
        {
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
    }
}
