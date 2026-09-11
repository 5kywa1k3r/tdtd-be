var advanced = AdvancedProjectionCases.Run() +
    AdvancedReaderOrderCases.Run();
var diff = DiffProjectionCases.Run();
var parity = await ExtendedOwnerParityCases.RunAsync();

Console.WriteLine(
    $"P10_EXTENDED_RAW_SOURCE_OK cases={advanced + diff + parity} " +
    "advancedGrains=3 typed=true options=true optionWhitespace=true " +
    "optionDuplicateFirstWins=true joinOrder=true whitespace=true " +
    "emptySum=true diffKinds=3 sideOnly=true multiReport=true " +
    "periods=true direction=true transitions=true doubleCollect=true " +
    "ownerParity=true failClosed=true");
