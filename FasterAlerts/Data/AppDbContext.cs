using FasterAlerts.Models;
using Microsoft.EntityFrameworkCore;

namespace FasterAlerts.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SentAlert>      SentAlerts     { get; set; }
    public DbSet<BacktestCache>  BacktestCache  { get; set; }
}
