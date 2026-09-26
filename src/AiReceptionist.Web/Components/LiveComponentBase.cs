using AiReceptionist.Core.Abstractions;
using AiReceptionist.Core.Data;
using AiReceptionist.Core.Scheduling;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Web.Components;

/// <summary>
/// Base class for dashboard pages that refresh themselves when the receptionist receives or sends
/// messages (via <see cref="IActivityNotifier"/>), so the admin sees activity in real time.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    [Inject] protected IActivityNotifier Notifier { get; set; } = default!;
    [Inject] protected IDbContextFactory<ReceptionistDbContext> DbFactory { get; set; } = default!;

    /// <summary>Business time zone used to display timestamps.</summary>
    protected TimeZoneInfo Tz { get; private set; } = TimeZoneInfo.Utc;

    protected override async Task OnInitializedAsync()
    {
        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            var settings = await db.Settings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync();
            Tz = TimeZoneResolver.Resolve(settings?.TimeZoneId);
        }
        Notifier.Changed += OnActivity;
        await LoadAsync();
    }

    protected abstract Task LoadAsync();

    /// <summary>Override to ignore events that do not affect the page.</summary>
    protected virtual bool ShouldReload(ActivityEvent evt) => true;

    private void OnActivity(ActivityEvent evt)
    {
        if (!ShouldReload(evt)) return;
        _ = InvokeAsync(async () =>
        {
            await LoadAsync();
            StateHasChanged();
        });
    }

    protected string Local(DateTime utc) => Ui.Local(utc, Tz);

    public virtual void Dispose()
    {
        Notifier.Changed -= OnActivity;
        GC.SuppressFinalize(this);
    }
}
