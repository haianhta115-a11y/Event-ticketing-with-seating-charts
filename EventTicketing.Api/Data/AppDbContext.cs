using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Role> Roles { get; set; }
    public DbSet<UserRole> UserRoles { get; set; }
    public DbSet<Seat> Seats { get; set; }
    public DbSet<SeatCategory> SeatCategories { get; set; }
    public DbSet<Performance> Performances { get; set; }
    public DbSet<Event> Events { get; set; }
    public DbSet<Showtime> Showtimes { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.PasswordHash).HasMaxLength(255);
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(ur => new { ur.UserId, ur.RoleId });

            entity.HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId);

            entity.HasOne(ur => ur.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId);
        });

        modelBuilder.Entity<SeatCategory>(entity =>
        {
            entity.ToTable("seat_categories");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.PerformanceId).HasColumnName("performance_id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(e => e.NormalizedName).HasColumnName("normalized_name").HasMaxLength(100);
            entity.HasAlternateKey(e => new { e.PerformanceId, e.Id });
            entity.HasIndex(e => new { e.PerformanceId, e.NormalizedName }).IsUnique();
        });

        modelBuilder.Entity<Seat>(entity =>
        {
            entity.ToTable("seats", table =>
            {
                table.HasCheckConstraint(
                    "CK_seats_hold_fields",
                    "status <> 'HELD' OR (held_by_user_id IS NOT NULL AND held_until IS NOT NULL)");
            });
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.PerformanceId).HasColumnName("performance_id");
            entity.Property(e => e.SeatCategoryId).HasColumnName("seat_category_id");
            entity.Property(e => e.Row).HasColumnName("row").HasMaxLength(50);
            entity.Property(e => e.Number).HasColumnName("number").HasMaxLength(50);
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion(
                    status => status.ToString().ToUpperInvariant(),
                    value => Enum.Parse<SeatStatus>(value, true))
                .HasMaxLength(20);
            entity.Property(e => e.HeldByUserId).HasColumnName("held_by_user_id");
            entity.Property(e => e.HeldUntil).HasColumnName("held_until");
            entity.HasIndex(e => new { e.PerformanceId, e.Row, e.Number }).IsUnique();
            entity.HasIndex(e => new { e.PerformanceId, e.Status, e.HeldUntil });

            entity.HasOne(e => e.SeatCategory)
                .WithMany(category => category.Seats)
                .HasForeignKey(e => new { e.PerformanceId, e.SeatCategoryId })
                .HasPrincipalKey(category => new { category.PerformanceId, category.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Performance>(entity =>
        {
            entity.ToTable("performances");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Title).HasColumnName("title").HasMaxLength(255);
            entity.Property(e => e.StartTime).HasColumnName("start_time");
            entity.Property(e => e.EndTime).HasColumnName("end_time");
            entity.Property(e => e.IsSalesClosed).HasColumnName("is_sales_closed");
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.ToTable("events", table =>
            {
                table.HasCheckConstraint("CK_events_status", "status IN ('DRAFT', 'PUBLISHED', 'ENDED')");
            });
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.Location).HasColumnName("location").HasMaxLength(300);
            entity.Property(e => e.ImageUrl).HasColumnName("image_url");
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion(
                    status => status.ToString().ToUpperInvariant(),
                    value => Enum.Parse<EventStatus>(value, true))
                .HasMaxLength(20);
            entity.Property(e => e.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        });

        modelBuilder.Entity<Showtime>(entity =>
        {
            entity.ToTable("showtimes", table =>
            {
                table.HasCheckConstraint("CK_showtimes_status", "status IN ('DRAFT', 'ONSALE', 'CLOSED')");
            });
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.StartTime).HasColumnName("start_time").HasColumnType("timestamp with time zone");
            entity.Property(e => e.EndTime).HasColumnName("end_time").HasColumnType("timestamp with time zone");
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion(
                    status => status.ToString().ToUpperInvariant(),
                    value => Enum.Parse<ShowtimeStatus>(value, true))
                .HasMaxLength(20);

            entity.HasIndex(e => new { e.Status, e.StartTime });

            entity.HasOne(e => e.Event)
                .WithMany(ev => ev.Showtimes)
                .HasForeignKey(e => e.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Seed Roles
        modelBuilder.Entity<Role>().HasData(
            new Role { Id = 1, Name = "Buyer" },
            new Role { Id = 2, Name = "Organizer" },
            new Role { Id = 3, Name = "GateStaff" },
            new Role { Id = 4, Name = "Accountant" },
            new Role { Id = 5, Name = "Admin" }
        );

        // Seed Users
        modelBuilder.Entity<User>().HasData(
            new User
            {
                Id = 1,
                Email = "admin@example.com",
                Name = "Admin User",
                PasswordHash = "$argon2id$v=19$m=1024,t=4,p=1$e/uWMrsUacwSe+MinnJdvw$bfqciaQS7Vh7mbrj4O9AYaIAkcSDBCN1ZA907doLVZE",
                Status = "Active",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new User
            {
                Id = 2,
                Email = "organizer@example.com",
                Name = "Organizer User",
                PasswordHash = "$argon2id$v=19$m=1024,t=4,p=1$7+KlHvQH2yi4qnai3Yi8xQ$PSFx2iT05KnChgWgouh1MgopqXrt+QXOUF0hMhv8TW8",
                Status = "Active",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        );

        modelBuilder.Entity<UserRole>().HasData(
            new UserRole { UserId = 1, RoleId = 5 }, // Admin
            new UserRole { UserId = 2, RoleId = 2 }  // Organizer
        );
    }
}
