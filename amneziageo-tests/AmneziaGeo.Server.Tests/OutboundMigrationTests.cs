using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Routing.Outbound;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AmneziaGeo.Server.Tests;

public class OutboundMigrationTests
{
    [Fact]
    public async Task OldAwgAndWsRowsSurviveTheSplitAndDowngrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261030101002_ConfigRequireUpdate";
        await migrator.MigrateAsync(previous);
        var columns = new List<(string Name, string Type, bool Required)>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(Outbounds)";
            await using var reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
            }
        }
        foreach (var kind in new[] { "local", "wg", "ws" })
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Outbounds (" + string.Join(",", columns.Select(c => "\"" + c.Name + "\""))
                + ") VALUES (" + string.Join(",", columns.Select(c => "@" + c.Name)) + ")";
            var id = kind == "local" ? 1 : kind == "wg" ? 2 : 3;
            foreach (var column in columns)
            {
                object value = column.Name switch
                {
                    "Id" or "Position" => id,
                    "Name" or "Kind" => kind,
                    "Mark" => 0xA600 + id,
                    "Table" => kind == "local" ? 254 : 42600 + id,
                    "IsEnabled" => 1,
                    "CreatedUtc" or "UpdatedUtc" => "2026-09-06 12:00:00+00:00",
                    "Host" => "exit.example.net",
                    "PrivateKey" => "private-" + kind,
                    "PeerKey" => "peer-" + kind,
                    "PresharedKey" => "preshared-" + kind,
                    "Address" => "10.8.1.5/32",
                    "Proxy" => kind == "ws" ? "wss://proxy.example.net:443" : "",
                    "Mtu" => 1380,
                    "Jc" => 7,
                    "HeaderProtectionKey" => "header-" + kind,
                    _ => column.Required ? column.Type == "TEXT" ? "" : 0 : DBNull.Value,
                };
                insert.Parameters.AddWithValue("@" + column.Name, value);
            }
            await insert.ExecuteNonQueryAsync();
        }
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var rows = await db.Outbounds.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.IsType<LocalOutboundEntity>(rows[0]);
        foreach (var row in rows.Skip(1))
        {
            var awg = Assert.IsType<AwgOutboundEntity>(row);
            var config = Assert.IsType<AwgOutboundConfig>(awg.ToConfig());
            Assert.Equal("private-" + row.Kind, config.PrivateKey);
            Assert.Equal("peer-" + row.Kind, config.PeerKey);
            Assert.Equal("preshared-" + row.Kind, config.PresharedKey);
            Assert.Equal(1380, config.Mtu);
            Assert.Equal(7, config.Obfuscation.Jc);
            Assert.Equal("header-" + row.Kind, config.Obfuscation.HeaderProtectionKey);
            Assert.Equal(row.Kind == "ws" ? "wss://proxy.example.net:443" : "", config.Proxy);
        }
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT name FROM pragma_table_info('Outbounds')";
            await using var reader = await schema.ExecuteReaderAsync();
            var common = new List<string>();
            while (await reader.ReadAsync())
            {
                common.Add(reader.GetString(0));
            }
            Assert.DoesNotContain("PrivateKey", common);
            Assert.DoesNotContain("Host", common);
            Assert.DoesNotContain("Jc", common);
        }
        await migrator.MigrateAsync(previous);
        await using var restored = connection.CreateCommand();
        restored.CommandText = "SELECT PrivateKey, HeaderProtectionKey FROM Outbounds WHERE Kind = 'ws'";
        await using var restoredReader = await restored.ExecuteReaderAsync();
        Assert.True(await restoredReader.ReadAsync());
        Assert.Equal("private-ws", restoredReader.GetString(0));
        Assert.Equal("header-ws", restoredReader.GetString(1));
    }
}
