using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Core.Data;

public sealed record ResetResult(int Contacts, int Conversations, int Messages, int Appointments);

/// <summary>"Start fresh": removes customer history while keeping configuration (settings, intents, rules).</summary>
public static class DataReset
{
    public static async Task<ResetResult> ClearHistoryAsync(ReceptionistDbContext db, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var messages = await db.Messages.ExecuteDeleteAsync(ct);
        var appointments = await db.Appointments.ExecuteDeleteAsync(ct);
        var conversations = await db.Conversations.ExecuteDeleteAsync(ct);
        var contacts = await db.Contacts.ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return new ResetResult(contacts, conversations, messages, appointments);
    }
}
