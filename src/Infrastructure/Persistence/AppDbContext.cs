using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Infrastructure.Persistence;

/// <summary>Shell context: no entities yet. T-008 adds tenancy, T-009 adds Identity.</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
