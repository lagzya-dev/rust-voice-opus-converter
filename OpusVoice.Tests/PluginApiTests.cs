using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Carbon.Plugins;
using Xunit;

namespace OpusVoice.Tests
{
    /// <summary>
    /// Carbon only routes Call()/CallHook()/Interface.Oxide.CallHook() to methods it has cached as hooks. Per
    /// Carbon.Base.BaseHookable.BuildHookCache (verified by decompiling Carbon), that cache is built from every
    /// non-public instance method - any name - plus public methods explicitly tagged [HookMethod]. A plain public
    /// method with no attribute is invisible to it. This plugin follows the same convention as its other hooks
    /// (e.g. OnPlayerDisconnected) and keeps its cross-plugin API methods private; this test guards that.
    ///
    /// It reads the compiled plugin's raw metadata (System.Reflection.Metadata) instead of using typeof/reflection,
    /// so it never has to load CarbonPlugin's base types (Plugin, BaseHookable, ...) into this process - those live
    /// in Rust/Carbon assemblies that are deliberately kept out of every build output here, since redistributing
    /// game files would not be allowed (see OpusVoice.PluginCheck.csproj).
    /// </summary>
    public class PluginApiTests
    {
        [Theory]
        [InlineData("PlayVoiceFile")]
        [InlineData("PlayVoiceFileForAll")]
        [InlineData("StopVoiceStream")]
        public void ApiMethod_IsNonPublic_SoCarbonCanCallItAcrossPlugins(string methodName)
        {
            // SteamVoicePacker lives in the same compiled assembly as Carbon.Plugins.OpusVoice (both come from
            // plugin/OpusVoice.cs via OpusVoice.PluginCheck), and referencing it does not touch any Carbon type.
            string pluginAssemblyPath = typeof(SteamVoicePacker).Assembly.Location;

            using FileStream stream = File.OpenRead(pluginAssemblyPath);
            using var peReader = new PEReader(stream);
            MetadataReader reader = peReader.GetMetadataReader();

            MethodDefinition method = FindMethod(reader, "Carbon.Plugins", "OpusVoice", methodName);

            bool isPublic = (method.Attributes & MethodAttributes.Public) != 0;
            Assert.False(isPublic,
                $"{methodName} is public. In Carbon, a public method needs [HookMethod] to be callable via Call()/CallHook() - " +
                "making it private/internal/protected instead (like the plugin's other hooks) is simpler and needs no attribute.");
        }

        private static MethodDefinition FindMethod(MetadataReader reader, string ns, string typeName, string methodName)
        {
            foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(typeHandle);
                if (reader.GetString(type.Namespace) != ns || reader.GetString(type.Name) != typeName)
                {
                    continue;
                }

                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                    if (reader.GetString(method.Name) == methodName)
                    {
                        return method;
                    }
                }

                throw new InvalidOperationException($"{ns}.{typeName} has no method named '{methodName}'.");
            }

            throw new InvalidOperationException($"Type {ns}.{typeName} was not found in the compiled plugin assembly.");
        }
    }
}
