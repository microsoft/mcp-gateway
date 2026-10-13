using System.Net;
using k8s.Autorest;
using Microsoft.McpGateway.Management.Contracts;

namespace Microsoft.McpGateway.Management.Deployment
{
    public static class DeploymentOwnership
    {
        public static bool Matches(IDictionary<string, string>? labels, string name, ResourceType resourceType) =>
            labels != null &&
            labels.TryGetValue("adapter/type", out var type) && type == resourceType.ToString().ToLowerInvariant() &&
            labels.TryGetValue("adapter/name", out var resourceName) && resourceName == name;

        public static async Task VerifyServiceAsync(IKubeClientWrapper kubeClient, string name, ResourceType resourceType, string namespaceName, CancellationToken cancellationToken)
        {
            var statefulSet = await IgnoreNotFoundAsync(() => kubeClient.ReadStatefulSetAsync(name, namespaceName, cancellationToken)).ConfigureAwait(false);
            if (statefulSet == null || statefulSet.Metadata?.DeletionTimestamp != null ||
                !Matches(statefulSet.Spec?.Selector?.MatchLabels, name, resourceType))
                throw new UnauthorizedAccessException("The deployment does not belong to this resource.");

            var service = await IgnoreNotFoundAsync(() => kubeClient.ReadServiceAsync($"{name}-service", namespaceName, cancellationToken)).ConfigureAwait(false);
            if (service == null || service.Metadata?.DeletionTimestamp != null ||
                !Matches(service.Spec?.Selector, name, resourceType))
                throw new UnauthorizedAccessException("The service does not belong to this resource.");
        }

        private static async Task<T?> IgnoreNotFoundAsync<T>(Func<Task<T>> operation) where T : class
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }
    }
}