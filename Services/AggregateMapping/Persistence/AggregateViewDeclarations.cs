using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed partial class AggregateCommandService
{
    private async Task<AggregateCommandResult> SaveViewDeclaration(IAggregateTransaction tx, AggregateCommitAuthority authority,
        AggregateCommandContext command, AggregateDataWindowDeclarationDto declaration, string confirmation, CancellationToken ct)
    {
        ValidateDeclaration(declaration);
        var context = authority.Read.Context;
        var id = DeclarationKey(context);
        var current = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, id, ct);
        if ((current?.Value.Declaration.Revision ?? 0) != declaration.Revision - 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        tokens.Verify(confirmation, Confirmation(command, "DATA_WINDOW", id, declaration,
            AggregateCanonical.Hash(new { current, authority.Read.Revisions, authority.Pins }), command.Now.AddMinutes(5)), command.Now);
        await tx.FenceAsync(authority, [], [], ct);
        await tx.PutAsync(AggregateCollections.Declarations, id, current?.Version ?? 0,
            new AggregateDeclarationState("VIEW", context.View!.ViewId, context.WorkId, context.AssignmentId, declaration), context.WorkId, context.View.ViewId, [], ct);
        foreach (var instance in await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances, new(context.WorkId, context.View.ViewId), ct))
            if (instance.Value.State == "DRAFT") await AggregateRefreshService.EnqueueAsync(tx, instance.Value, "DATA_WINDOW:" + command.CommandId, ct);
        return new(context.View.ViewId, declaration.Revision, authority.Read.Revisions.PayloadRevision, 0, "DATA_WINDOW_SAVED");
    }
}
