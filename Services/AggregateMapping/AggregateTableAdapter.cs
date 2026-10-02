using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateTableAdapter
{
    // Coordinates address value cells only. Headers never enter an AggregateTable's Rows.
    internal static AggregateChannel FilterRegion(AggregateChannel input, AggregateTableAreaDto area, int columnIndex,
        AggregateExpressionDto predicate, AggregateEvaluator evaluator, AggregateBudget budget)
    {
        var output = new List<AggregateObservation>();
        foreach (var original in input.Items)
        {
            var source = original.Value.Table ?? throw new AggregatePreviewException("AGG_TABLE_VALUE_INVALID");
            var sourceColumn = (source.Schema.ColumnCoordinates ?? Enumerable.Range(1, source.Schema.Columns.Count).ToArray()).ToList().IndexOf(columnIndex);
            if (sourceColumn < 0) throw new AggregatePreviewException("AGG_TABLE_RANGE_BOUNDS");
            var sourceRows = Coordinates(area, source.Rows.Count, source.Schema.Columns.Count).Rows;
            var item = Select(new("TABLE", "SINGLE", [original], true), area, budget).Items.Single();
            var table = item.Value.Table!;
            var rows = new List<IReadOnlyList<AggregateCell>>(); var units = new List<string>();
            foreach (var (row, index) in table.Rows.Select((row, index) => (row, index)))
            {
                budget.Spend(row.Count, row.Count * 96L);
                var cell = source.Rows[sourceRows[index]][sourceColumn];
                var type = evaluator.Infer(predicate, new Dictionary<string, AggregateExpressionType> { ["cell"] = new(cell.Value.Type, "SINGLE", true) });
                if (type is not { Type: "BOOLEAN", Shape: "SINGLE" }) throw new AggregatePreviewException("AGG_FILTER_TYPE");
                var match = AggregateEvaluator.Scalar(evaluator.Evaluate(predicate, new Dictionary<string, AggregateChannel> { ["cell"] = AggregateEvaluator.Single(cell.Value, cell.Trace, true) }));
                var keep = match.Value.State == "VALUE" && match.Value.Boolean == true;
                if (keep) { rows.Add(row.Select(c => c with { Trace = c.Trace.Concat(match.Trace).Distinct().ToArray() }).ToArray()); units.Add(table.Units[index]); }
                else if (table.Schema.Layout == "matrix")
                {
                    rows.Add(row.Select(c => new AggregateCell(AggregateValue.NoResult(c.Value.Type), c.Trace)).ToArray());
                    units.Add(table.Units[index]);
                }
            }
            if (rows.Count == 0)
            {
                // Row filtering does not change report cardinality. ONLY can still select
                // this one source report; its empty calculation region remains no result.
                output.Add(new(AggregateValue.NoResult("TABLE"), item.Trace));
                continue;
            }
            var schema = table.Schema with { Rows = Enumerable.Range(1, rows.Count).Select(i => i.ToString()).ToArray(),
                CellTypes = rows.Select(r => (IReadOnlyList<string>)r.Select(c => c.Value.Type).ToArray()).ToArray() };
            output.Add(new(new("TABLE", "VALUE", Table: new(schema, rows, units)), item.Trace));
        }
        return new("TABLE", input.Shape, output, true);
    }
    internal static (int[] Rows, int[] Columns) Coordinates(AggregateTableAreaDto area, int rows, int columns)
    {
        var r0 = area.RowStart ?? 1; var r1 = area.RowEnd ?? rows;
        var c0 = area.ColumnStart ?? 1; var c1 = area.ColumnEnd ?? columns;
        if (rows == 0 && area.Kind == "ALL") return ([], Enumerable.Range(0, columns).ToArray());
        if (rows == 0 && area.Kind == "COLUMN" && c0 >= 1 && c1 >= c0 && c1 <= columns)
            return ([], Enumerable.Range(c0 - 1, c1 - c0 + 1).ToArray());
        if (r0 < 1 || c0 < 1 || r1 > rows || c1 > columns || r1 < r0 || c1 < c0)
            throw new AggregatePreviewException("AGG_TABLE_RANGE_BOUNDS");
        return (Enumerable.Range(r0 - 1, r1 - r0 + 1).ToArray(), Enumerable.Range(c0 - 1, c1 - c0 + 1).ToArray());
    }
    internal static AggregateChannel Select(AggregateChannel channel, AggregateTableAreaDto area, AggregateBudget budget)
    {
        var selected = new List<AggregateObservation>();
        foreach (var item in channel.Items)
        {
            if (item.Value.State != "VALUE" || item.Value.Table == null) throw new AggregatePreviewException("AGG_TABLE_VALUE_INVALID");
            var table = item.Value.Table;
            var (rows, columns) = Coordinates(area, table.Rows.Count, table.Schema.Columns.Count);
            budget.Spend(rows.Length * columns.Length);
            var schema = table.Schema with { Columns = columns.Select(c => table.Schema.Columns[c]).ToArray(),
                Rows = rows.Select(r => (r + 1).ToString()).ToArray(),
                ColumnCoordinates = columns.Select(c => table.Schema.ColumnCoordinates?[c] ?? c + 1).ToArray(),
                CellTypes = rows.Select(r => (IReadOnlyList<string>)columns.Select(c => table.Rows[r][c].Value.Type).ToArray()).ToArray() };
            var value = new AggregateTable(schema, rows.Select(r => (IReadOnlyList<AggregateCell>)columns.Select(c => table.Rows[r][c]).ToArray()).ToArray(), rows.Select(r => table.Units[r]).ToArray());
            selected.Add(new(new("TABLE", "VALUE", Table: value), item.Trace));
        }
        return new("TABLE", channel.Shape, selected, true);
    }
    internal static AggregateChannel Append(AggregateChannel channel, AggregateBudget budget)
    {
        var tables = channel.Items.Where(i => i.Value.State == "VALUE").ToArray();
        if (tables.Length == 0) return AggregateEvaluator.Single(AggregateValue.NoResult("TABLE"), [], true);
        var schema = tables[0].Value.Table!.Schema;
        var rows = new List<IReadOnlyList<AggregateCell>>(); var units = new List<string>();
        foreach (var item in tables)
        {
            var table = item.Value.Table!;
            if (table.Schema.Identity != schema.Identity || !table.Schema.Columns.SequenceEqual(schema.Columns))
                throw new AggregatePreviewException("AGG_TABLE_SCHEMA_INCOMPATIBLE");
            budget.Spend(table.Rows.Count * schema.Columns.Count, table.Rows.Count * schema.Columns.Count * 96L);
            rows.AddRange(table.Rows); units.AddRange(table.Units);
        }
        var resultSchema = schema with { Rows = Enumerable.Range(1, rows.Count).Select(i => i.ToString()).ToArray(),
            CellTypes = rows.Select(r => (IReadOnlyList<string>)r.Select(c => c.Value.Type).ToArray()).ToArray() };
        return AggregateEvaluator.Single(new("TABLE", "VALUE", Table: new(resultSchema, rows, units)), channel.Items.SelectMany(i => i.Trace).ToArray(), true);
    }
    internal static long CountPresentRows(AggregateChannel channel, int columnIndex, AggregateBudget budget)
    {
        long count = 0;
        foreach (var item in channel.Items.Where(i => i.Value.State == "VALUE"))
        {
            var table = item.Value.Table!;
            var coordinate = (table.Schema.ColumnCoordinates ?? Enumerable.Range(1, table.Schema.Columns.Count).ToArray()).ToList().IndexOf(columnIndex);
            if (coordinate < 0) throw new AggregatePreviewException("AGG_TABLE_RANGE_BOUNDS");
            foreach (var row in table.Rows) { budget.Spend(); if (row[coordinate].Value.State == "VALUE") count++; }
        }
        return count;
    }
    internal static AggregateChannel Assign(AggregateTableSchema targetSchema, string outputId, IReadOnlyList<AggregateTableAssignmentDto> assignments,
        IReadOnlyDictionary<string, AggregateChannel> inputs, AggregateEvaluator evaluator, AggregateBudget budget)
    {
        if (targetSchema.Layout != "matrix") throw new AggregatePreviewException("AGG_TABLE_FIXED_TARGET_REQUIRED");
        var rows = targetSchema.Rows.Select((_, r) => targetSchema.Columns.Select((_, c) =>
            new AggregateCell(AggregateValue.Blank(targetSchema.CellTypes[r][c]), [])).ToArray()).ToArray();
        var trace = new List<AggregateTrace>();
        foreach (var assignment in assignments.Where(a => a.TargetOutputPortId == outputId))
        {
            var (targetRows, targetColumns) = Coordinates(assignment.Target, rows.Length, targetSchema.Columns.Count);
            if (assignment.Expression == null)
            {
                var source = AggregateEvaluator.Scalar(Select(inputs[assignment.SourceInputPortId!], assignment.Source!, budget));
                var sourceRows = source.Value.Table!.Rows;
                if (sourceRows.Count != targetRows.Length || sourceRows.Any(r => r.Count != targetColumns.Length)) throw new AggregatePreviewException("AGG_TABLE_SHAPE");
                for (var r = 0; r < targetRows.Length; r++) for (var c = 0; c < targetColumns.Length; c++) Write(targetRows[r], targetColumns[c], sourceRows[r][c]);
                trace.AddRange(source.Trace);
            }
            else
            {
                var inferred = evaluator.Infer(assignment.Expression, inputs.ToDictionary(p => p.Key, p => new AggregateExpressionType(p.Value.Type, p.Value.Shape, p.Value.TableOrigin)));
                if (HasScalarInput(assignment.Expression, inputs) || (inferred.Type == "TABLE" && !inferred.TableOrigin) || inferred.Shape != "SINGLE"
                    || (inferred.Type != "TABLE" && assignment.Target.Kind != "CELL"))
                    throw new AggregatePreviewException("AGG_FIELD_TABLE_BRIDGE_DEFERRED");
                var evaluated = AggregateEvaluator.Scalar(evaluator.Evaluate(assignment.Expression, inputs));
                if (inferred.Type == "TABLE")
                {
                    if (evaluated.Value.State == "NO_RESULT")
                    {
                        foreach (var row in targetRows) foreach (var column in targetColumns)
                            Write(row, column, new(AggregateValue.NoResult(targetSchema.CellTypes[row][column]), evaluated.Trace));
                    }
                    else
                    {
                        var sourceRows = evaluated.Value.Table?.Rows ?? throw new AggregatePreviewException("AGG_TABLE_VALUE_INVALID");
                        if (sourceRows.Count != targetRows.Length || sourceRows.Any(r => r.Count != targetColumns.Length))
                            throw new AggregatePreviewException("AGG_TABLE_SHAPE");
                        for (var r = 0; r < targetRows.Length; r++) for (var c = 0; c < targetColumns.Length; c++)
                            Write(targetRows[r], targetColumns[c], sourceRows[r][c]);
                    }
                }
                else Write(targetRows[0], targetColumns[0], new(evaluated.Value, evaluated.Trace));
                trace.AddRange(evaluated.Trace);
            }
        }
        return AggregateEvaluator.Single(new("TABLE", "VALUE", Table: new(targetSchema, rows, Enumerable.Repeat("", rows.Length).ToArray())), trace, true);
        void Write(int row, int column, AggregateCell value)
        {
            budget.Spend();
            if (value.Value.Type != targetSchema.CellTypes[row][column]) throw new AggregatePreviewException("AGG_TABLE_CELL_TYPE");
            rows[row][column] = value; // Ordered assignments: last write wins only in the overlap.
        }
    }
    internal static bool HasScalarInput(AggregateExpressionDto expression, IReadOnlyDictionary<string, AggregateChannel> inputs)
        => (expression.Kind == "INPUT" && inputs[expression.Ref!].Type != "TABLE")
            || (expression.Arguments?.Any(e => HasScalarInput(e, inputs)) ?? false);
}
