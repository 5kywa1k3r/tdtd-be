using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;

// CPU/allocation exploration only: deliberately returns before Mongo initialization.
if (args.SequenceEqual(new[] { "--native-untouched-target" })) { NativeUntouchedTargetChecks.Run(); return; }
if (args.SequenceEqual(new[] { "--text-probe" })) { TextLoadProbe.Run(); return; }
if(args.Length==4&&args[0]=="--database"&&args[1]=="tdtd"&&args[2]=="--read-list-l4-state"){await ListL4StateRead.Run(args[3]);return;}
if(args.Length==5&&args[0]=="--database"&&args[1]=="tdtd"&&args[2]=="--probe-list-l4-acl"){await ListL4AclProbe.Run(args[3],args[4]);return;}
if(args.Length==4&&args[0]=="--database"&&args[1]=="tdtd"&&args[2]=="--prepare-operators-v3-browser"){await ExtendedBrowserFixture.Prepare(args[3]);return;}
if (args.SequenceEqual(new[] { "--database", "tdtd", "--provenance-inspect" })) { await P05ProvenanceRepair.Inspect(); return; }
if (args.SequenceEqual(new[] { "--database", "tdtd", "--provenance-rehearsal" })) { await P05ProvenanceRepair.Repair(false); return; }
if (args.SequenceEqual(new[] { "--database", "tdtd", "--provenance-repair" })) { await P05ProvenanceRepair.Repair(true); return; }
if (args.SequenceEqual(new[] { "--database", "tdtd", "--provenance-verify" })) { await P05ProvenanceRepair.VerifyRepair(); return; }
if (args.SequenceEqual(new[] { "--database", "tdtd", "--provenance-final" })) { await P05ProvenanceRepair.VerifyRepair(true); return; }
if (args.SequenceEqual(new[] { "--database", "tdtd", "--provenance-startup" })) { await ProvenanceStartupChecks.Run(); return; }

var browserMode = args.SequenceEqual(new[] { "--database", "tdtd", "--browser" });
var loadMode = args.SequenceEqual(new[] { "--database", "tdtd", "--load" });
var wideLoadMode = args.SequenceEqual(new[] { "--database", "tdtd", "--wide-load" });
var mixedLoadMode = args.SequenceEqual(new[] { "--database", "tdtd", "--mixed-load" });
var contentStoreMode = args.SequenceEqual(new[] { "--database", "tdtd", "--content-store" });
var contentApiMode = args.SequenceEqual(new[] { "--database", "tdtd", "--content-api" });
var contentRetryMode = args.SequenceEqual(new[] { "--database", "tdtd", "--content-retry" });
var listMode = args.SequenceEqual(new[] { "--database", "tdtd", "--list-l3" });
var operatorsMode = args.SequenceEqual(new[] { "--database", "tdtd", "--operators-v3" });
var listL4Periodic = args.SequenceEqual(new[] { "--database", "tdtd", "--list-l4-periodic-fixture" });
var listL4Paging = args.SequenceEqual(new[] { "--database", "tdtd", "--list-l4-paging-fixture" });
var listL4FixtureMode = listL4Periodic || listL4Paging || args.SequenceEqual(new[] { "--database", "tdtd", "--list-l4-fixture" });
var prepareListL4 = args.Length==4&&args[0]=="--database"&&args[1]=="tdtd"&&args[2]=="--prepare-list-l4";
var prepareBrowser = args.Length == 4 && args[0] == "--database" && args[1] == "tdtd" && args[2] == "--prepare-browser-slots";
if (!operatorsMode && !listMode && !listL4FixtureMode && !prepareListL4 && !browserMode && !prepareBrowser && !loadMode && !wideLoadMode && !mixedLoadMode && !contentStoreMode && !contentApiMode && !contentRetryMode && !args.SequenceEqual(new[] { "--database", "tdtd", "--run" }))
    throw new ArgumentException("Use --database tdtd --run or --browser for the authorized P05 fixtures.");
var db = new MongoDbContext(Options.Create(new MongoOptions {
    ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = "tdtd" }));
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(loadMode || wideLoadMode || mixedLoadMode ? 15 : operatorsMode ? 8 : 3));
var hello = await db.Db.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: timeout.Token);
if (hello.GetValue("setName", "") != "tdtd-rs" || !hello.GetValue("isWritablePrimary", false).AsBoolean)
    throw new InvalidOperationException("P05 requires the authorized writable replica set");
var runId = (operatorsMode ? "operators-v3-" : listL4FixtureMode ? "list-l4-" : listMode ? "list-l3-" : "p05-") + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
Console.WriteLine($"RUN {runId} database={db.Db.DatabaseNamespace.DatabaseName} replicaSet=tdtd-rs");
try
{
    if(prepareListL4){await ListL4BrowserFixture.Prepare(db,args[3],timeout.Token);return;}
    if(operatorsMode){
        await FixtureProvenanceChecks.Migration(db,"operators v3 before own fixtures",timeout.Token);
        await ApiChecks.Run(db,runId,timeout.Token,listOnly:true,operatorsV3:true);
        await FixtureProvenanceChecks.Migration(db,"operators v3 after runtime",timeout.Token);
        Console.WriteLine("PASS operators v3 scoped API/job/Apply/readback; not browser acceptance or P05 closure.");return;
    }
    if (listMode) {
        await FixtureProvenanceChecks.Migration(db, "L3 before fixture", timeout.Token);
        await ApiChecks.Run(db, runId, timeout.Token, listOnly: true);
        await FixtureProvenanceChecks.Migration(db, "L3 after runtime", timeout.Token);
        Console.WriteLine("PASS L3 scoped API/job/lifecycle and Table regression complete; fixtures retained; no browser UAT claim.");
        return;
    }
    if (listL4FixtureMode) {
        await FixtureProvenanceChecks.Migration(db, "L4 before isolated fixture", timeout.Token);
        await ApiChecks.Run(db, runId, timeout.Token, listL4FixtureOnly: true,listL4Periodic:listL4Periodic,listL4Paging:listL4Paging);
        await FixtureProvenanceChecks.Migration(db, "L4 after isolated fixture", timeout.Token);
        Console.WriteLine($"PASS L4 isolated {(listL4Paging ? "22+23" : "2+3")} List fixture retained; no browser acceptance claim.");
        return;
    }
    if (prepareBrowser) { await BrowserHost.SeedMissingSlotDeclarations(db, args[3], timeout.Token); return; }
    if (browserMode) { await BrowserHost.Run(db, runId); return; }
    if (contentStoreMode) { await ContentTableStoreChecks.Run(db, runId, timeout.Token); return; }
    if (contentApiMode) { await ApiChecks.Run(db, runId, timeout.Token, contentOnly: true); return; }
    if (contentRetryMode) { await ApiChecks.Run(db, runId, timeout.Token, contentOnly: true, contentRetry: true); return; }
    if (wideLoadMode) { await ApiChecks.Run(db, runId, timeout.Token, wideLoadOnly: true); return; }
    if (mixedLoadMode) { await ApiChecks.Run(db, runId, timeout.Token, mixedLoadOnly: true); return; }
    if (loadMode) { await ApiChecks.Run(db, runId, timeout.Token, loadOnly: true); return; }
    await MongoStoreChecks.Run(db, runId, timeout.Token);
    await ApiChecks.Run(db, runId, timeout.Token);
    Console.WriteLine("PASS P05 runtime slice complete; fixtures retained; no database/collection dropped.");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
