// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using k8s;
using k8s.Models;
using Microsoft.McpGateway.Management.Deployment;

namespace Microsoft.McpGateway.Service.Routing
{
    public class AdapterKubernetesNodeInfoProvider : IServiceNodeInfoProvider
    {
        private const string AdapterNamespace = "adapter";
        private const string AdapterLabel = "adapter/type=mcp";
        private const string RunningField = "status.phase=Running";
        private const int AdapterListenerPort = 8000;

        private readonly ConcurrentDictionary<string, string[]> _healthyPodsByStatefulSet = new();
        private readonly IKubernetesClientFactory _kubeClientFactory;
        private readonly ILogger<AdapterKubernetesNodeInfoProvider> _logger;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly TaskCompletionSource<bool> _initialFetchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed = false;

        public AdapterKubernetesNodeInfoProvider(IKubernetesClientFactory kubeClientFactory, ILogger<AdapterKubernetesNodeInfoProvider> logger)
        {
            _kubeClientFactory = kubeClientFactory ?? throw new ArgumentNullException(nameof(kubeClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            FetchPodAddressInfo();
        }


        public async Task<IDictionary<string, string>> GetNodeAddressesAsync(string adapterName, CancellationToken cancellationToken)
        {
            await _initialFetchCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            var healthyPods = GetHealthyPods(adapterName).ToDictionary(
                p => p,
                p => $"http://{p}.{adapterName}-service.{AdapterNamespace}.svc.cluster.local:{AdapterListenerPort}");

            return healthyPods;
        }

        private string[] GetHealthyPods(string statefulSetName) => _healthyPodsByStatefulSet.TryGetValue(statefulSetName, out var pods) ? pods : [];

        private static bool IsPodReady(V1Pod pod) => pod.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;

        private void FetchPodAddressInfo()
        {
            _ = Task.Run(async () =>
            {
                _logger.LogInformation("Start to update healthy pod map");

                var cancellationToken = _cancellationTokenSource.Token;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var kubeClient = await _kubeClientFactory.GetKubernetesClientAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var pods = await kubeClient.CoreV1.ListNamespacedPodAsync(
                            namespaceParameter: AdapterNamespace,
                            labelSelector: AdapterLabel,
                            fieldSelector: RunningField,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        // Initialize the healthy pod dictionary.
                        var readyPods = pods.Items
                            .Where(p =>
                                p.Metadata?.Name != null &&
                                p.Metadata.Labels.TryGetValue("statefulset.kubernetes.io/pod-name", out var podName) &&
                                p.Metadata.OwnerReferences?.Any(o => o.Kind == "StatefulSet") == true)
                            .GroupBy(p => p.Metadata.OwnerReferences.First(o => o.Kind == "StatefulSet").Name)
                            .ToDictionary(
                                g => g.Key,
                                g => g.Select(p => p.Metadata.Name!).ToArray());

                        foreach (var kvp in readyPods)
                        {
                            _healthyPodsByStatefulSet[kvp.Key] = kvp.Value;
                        }

                        foreach (var kvp in _healthyPodsByStatefulSet)
                        {
                            _healthyPodsByStatefulSet[kvp.Key] = kvp.Value;
                            Console.WriteLine($"{kvp.Key}: [{string.Join(", ", kvp.Value)}]");
                        }

                        _logger.LogInformation("Kubernetes info initial fetching has completed, total {count} healthy pods.", readyPods.Count);

                        if (!_initialFetchCompleted.Task.IsCompleted)
                            _initialFetchCompleted.TrySetResult(true);

                        _logger.LogInformation("Start Kubernetes watch for pod events");

                        var watcherEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var watcher = kubeClient.CoreV1.WatchListNamespacedPod(
                            namespaceParameter: AdapterNamespace,
                            labelSelector: AdapterLabel,
                            fieldSelector: RunningField,
                            resourceVersion: pods.Metadata?.ResourceVersion,
                            onEvent: (eventType, pod) =>
                            {
                                _logger.LogInformation("Receive Kubernetes watch event type {eventType}, pod name {name}", eventType, pod.Metadata?.Name);

                                if (pod.Metadata?.Name == null)
                                    return;

                                var adapterName = pod.Metadata.OwnerReferences.SingleOrDefault(o => o.Kind == "StatefulSet")?.Name;
                                if (adapterName == null)
                                    return;

                                _healthyPodsByStatefulSet.AddOrUpdate(
                                    adapterName,
                                    key => eventType == WatchEventType.Added && IsPodReady(pod) ? [pod.Metadata!.Name!] : [],
                                    (key, existingHealthyPods) =>
                                    {
                                        var healthyPodsList = existingHealthyPods.ToList();
                                        var podName = pod.Metadata!.Name!;

                                        switch (eventType)
                                        {
                                            case WatchEventType.Added:
                                            case WatchEventType.Modified:
                                                if (IsPodReady(pod) && !healthyPodsList.Contains(podName))
                                                    healthyPodsList.Add(podName);
                                                else if (!IsPodReady(pod))
                                                    healthyPodsList.Remove(podName);
                                                break;

                                            case WatchEventType.Deleted:
                                                healthyPodsList.Remove(podName);
                                                break;
                                        }

                                        return [.. healthyPodsList];
                                    });

                                _logger.LogInformation("Kubernetes watch event type {eventType}, pod name {name}, update completes", eventType, pod.Metadata?.Name);
                            },
                            onError: ex =>
                            {
                                Console.WriteLine($"Watch error: {ex}");
                                _logger.LogError(ex, "Kubernetes watch encountered an error.");
                                watcherEnd.TrySetResult(true);
                            },
                            onClosed: () =>
                            {
                                _logger.LogWarning("Kubernetes watch is closed.");
                                watcherEnd.TrySetResult(true);
                            });

                        await watcherEnd.Task.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to update healthy pod map {message}", ex.Message);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
            });
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }

            _disposed = true;
        }
    }
}
