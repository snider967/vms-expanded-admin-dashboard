using Jellyfin.Data.Events;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal sealed class DeskTaskRegistration(ITaskManager inner) : ITaskManager
{
    internal static void FilterLegacyTasks(IServiceCollection services)
    {
        // Jellyfin has already discovered task types when it registers plugin services.
        // Keep its task manager and filter VMS duplicates before it creates workers.
        var descriptor = services.LastOrDefault(service => service.ServiceType == typeof(ITaskManager));
        if (descriptor?.Lifetime == ServiceLifetime.Singleton
            && descriptor.ImplementationType is { } implementation
            && implementation.FullName == "Emby.Server.Implementations.ScheduledTasks.TaskManager")
        {
            services[services.IndexOf(descriptor)] = ServiceDescriptor.Singleton<ITaskManager>(provider =>
                new DeskTaskRegistration((ITaskManager)ActivatorUtilities.CreateInstance(provider, implementation)));
        }
    }

    public void AddTasks(IEnumerable<IScheduledTask> tasks)
    {
        var all = tasks.ToArray();
        var assemblyName = typeof(Plugin).Assembly.GetName().Name;
        var duplicates = all.Where(task => task.GetType().Assembly.GetName().Name == assemblyName)
            .GroupBy(task => task.GetType().FullName, StringComparer.Ordinal)
            .SelectMany(group => group.OrderByDescending(task => task.GetType().Assembly.GetName().Version)
                .ThenBy(task => task.GetType().Assembly.Location, StringComparer.Ordinal).Skip(1))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        inner.AddTasks(all.Where(task => !duplicates.Contains(task)));
    }

    public event EventHandler<GenericEventArgs<IScheduledTaskWorker>>? TaskExecuting
    {
        add => inner.TaskExecuting += value;
        remove => inner.TaskExecuting -= value;
    }

    public event EventHandler<TaskCompletionEventArgs>? TaskCompleted
    {
        add => inner.TaskCompleted += value;
        remove => inner.TaskCompleted -= value;
    }

    public IReadOnlyList<IScheduledTaskWorker> ScheduledTasks => inner.ScheduledTasks;
    public void CancelIfRunningAndQueue<T>(TaskOptions options) where T : IScheduledTask => inner.CancelIfRunningAndQueue<T>(options);
    public void CancelIfRunningAndQueue<T>() where T : IScheduledTask => inner.CancelIfRunningAndQueue<T>();
    public void CancelIfRunning<T>() where T : IScheduledTask => inner.CancelIfRunning<T>();
    public void QueueScheduledTask<T>(TaskOptions options) where T : IScheduledTask => inner.QueueScheduledTask<T>(options);
    public void QueueScheduledTask<T>() where T : IScheduledTask => inner.QueueScheduledTask<T>();
    public void QueueIfNotRunning<T>() where T : IScheduledTask => inner.QueueIfNotRunning<T>();
    public void QueueScheduledTask(IScheduledTask task, TaskOptions options) => inner.QueueScheduledTask(task, options);
    public void Cancel(IScheduledTaskWorker task) => inner.Cancel(task);
    public Task Execute(IScheduledTaskWorker task, TaskOptions options) => inner.Execute(task, options);
    public void Execute<T>() where T : IScheduledTask => inner.Execute<T>();
    public void Dispose() => inner.Dispose();
}
