using k8s;

namespace IncludeDamon.Utilities;

internal static class Utilities
{
    public static KubernetesClientConfiguration CreateKubernetesClientConfiguration()
    {
        try
        {
            // Try in-cluster config first
            return KubernetesClientConfiguration.InClusterConfig();
        }
        catch (Exception)
        {
            // Fallback to local kubeconfig
            // This will use:
            //  - KUBECONFIG env var if set, OR
            //  - ~/.kube/config as default
            return KubernetesClientConfiguration.BuildDefaultConfig();
        }
    }
}
