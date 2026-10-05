using ConnectX.Server.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace ConnectX.Server.Models.Contexts;

public sealed class RoomOpsHistoryContext : DbContext
{
    public RoomOpsHistoryContext(DbContextOptions<RoomOpsHistoryContext> option) : base(option)
    {
    }

    public DbSet<RoomCreateHistory> RoomCreateHistories { get; set; }

    public DbSet<RoomJoinHistory> RoomJoinHistories { get; set; }
}