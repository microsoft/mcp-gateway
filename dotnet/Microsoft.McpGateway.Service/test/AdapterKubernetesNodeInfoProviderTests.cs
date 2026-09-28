using k8s;
using k8s.Models;
using Microsoft.McpGateway.Service.Routing;

namespace Microsoft.McpGateway.Service.Tests;

[TestClass]
public class AdapterKubernetesNodeInfoProviderTests
{
    [DataTestMethod]
    [DataRow(WatchEventType.Added)]
    [DataRow(WatchEventType.Modified)]
    public void ApplyPodEvent_AddsReadyPodWithoutExistingEntry(WatchEventType eventType)
    {
        var pods = AdapterKubernetesNodeInfoProvider.ApplyPodEvent([], eventType, CreatePod(ready: true));

        CollectionAssert.AreEqual(new[] { "adapter-0" }, pods);
    }

    [DataTestMethod]
    [DataRow(WatchEventType.Modified, false, false)]
    [DataRow(WatchEventType.Modified, true, true)]
    [DataRow(WatchEventType.Deleted, true, false)]
    public void ApplyPodEvent_RemovesPodThatCannotServe(WatchEventType eventType, bool ready, bool terminating)
    {
        var pods = AdapterKubernetesNodeInfoProvider.ApplyPodEvent(["adapter-0", "adapter-1"], eventType, CreatePod(ready, terminating));

        CollectionAssert.AreEqual(new[] { "adapter-1" }, pods);
    }

    private static V1Pod CreatePod(bool ready, bool terminating = false) => new()
    {
        Metadata = new V1ObjectMeta { Name = "adapter-0", DeletionTimestamp = terminating ? DateTime.UtcNow : null },
        Status = new V1PodStatus { Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }] }
    };
}
