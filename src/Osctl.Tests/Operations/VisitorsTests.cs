// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Osctl.Operations;
using Osctl.Tests.Instances;

namespace Osctl.Tests.Operations;

public class VisitorsTests
{
    // Lines as OpenSim master writes them (from the SpatialAudioServer test standalone's log, addresses changed).
    private const string Log = """
        2026-11-14 20:40:40,100 INFO  [LLOGIN SERVICE]: Login request for Test One at last using viewer Firestorm-Releasex64 7.2.2.79439, channel Firestorm-Releasex64, IP 192.0.2.7, Mac 855fcb2d, Id0 96714849 
        2026-11-14 20:40:41,200 DEBUG [SCENE]: Incoming client Test One in region SAS Test via regular login. Client IP verification not performed.
        2026-11-14 20:40:49,469 INFO  [SCENE PRESENCE]: Complete movement of Test One into SAS Test <128, 128, 0>
        2026-11-14 20:52:01,000 INFO  [SCENE PRESENCE]: Complete movement of Test One into SAS Test2 <3.5, 120.25, 22.1>
        2026-11-14 21:00:00,000 DEBUG [SCENE]: Incoming client Jane.Doe @grid.example.org:8002 in region SAS Test via HG login
        2026-11-14 21:00:05,000 INFO  [SCENE PRESENCE]: Complete movement of Jane.Doe @grid.example.org:8002 into SAS Test <128, 128, 25>
        not a log line
        """;

    [Fact]
    public void ArrivalsAreReadWithHowTheyCame()
    {
        using var dir = new TempDir();
        var file = dir.Write("OpenSim.log", Log);

        var visits = Visitors.Read("A", [file]).ToList();

        Assert.Equal(3, visits.Count);
        Assert.Equal(new Visit(new DateTime(2026, 11, 14, 20, 40, 49), "A", "SAS Test", "Test One", Visitors.Login, "192.0.2.7"), visits[0]);
        Assert.Equal(("SAS Test2", Visitors.Arrival, (string?)null), (visits[1].Region, visits[1].How, visits[1].Ip));
        Assert.Equal(("Jane.Doe @grid.example.org:8002", Visitors.HgLogin), (visits[2].Name, visits[2].How));
    }

    [Fact]
    public async Task TheCommandReadsRotatedLogsAndFilters()
    {
        using var f = new InstanceFixture();
        await f.Ok("instance", "add", "A", "--estate-owner", "Test User");
        var logs = f.Instances.LogDir("A");
        Directory.CreateDirectory(logs);
        await using (var gz = new GZipStream(File.Create(Path.Combine(logs, "OpenSim.log.2026-11-13.gz")), CompressionLevel.Fastest))
        await using (var writer = new StreamWriter(gz))
            await writer.WriteAsync("2026-11-13 10:00:00,000 INFO  [SCENE PRESENCE]: Complete movement of Old Visitor into SAS Test <1, 2, 3>\n");
        File.SetLastWriteTimeUtc(Path.Combine(logs, "OpenSim.log.2026-11-13.gz"), new DateTime(2026, 11, 13, 23, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(logs, "OpenSim.log"), Log);

        var table = await f.Ok("visitors");
        Assert.Contains("Old Visitor", table);
        Assert.True(table.IndexOf("Old Visitor", StringComparison.Ordinal) < table.IndexOf("Test One", StringComparison.Ordinal));

        var csv = await f.Ok("visitors", "A", "--since", "2026-11-14 21:00", "--csv");
        Assert.Equal("time,instance,region,name,how,ip\n2026-11-14 21:00:05,A,SAS Test,Jane.Doe @grid.example.org:8002,hg-login,\n", csv);
    }
}
