using System.Reflection;
using System.Runtime.Loader;

namespace AmGateway.PluginHost;

/// <summary>
/// 插件加载上下文 - 每个插件一个实例，实现依赖隔离
/// 共享程序集（Abstractions、Microsoft.Extensions.*）回退到默认上下文
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginDllPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginDllPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 共享程序集回退到 DefaultLoadContext，确保主机和插件使用同一类型
        if (IsSharedAssembly(assemblyName.Name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // 原生 DLL 加载（如 libplctag 的 plctag.dll）
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    private static bool IsSharedAssembly(string? name) =>
        name != null && (
            name == "AmGateway.Abstractions" ||
            name.StartsWith("Microsoft.Extensions.") ||
            name.StartsWith("System.")
        );
}
