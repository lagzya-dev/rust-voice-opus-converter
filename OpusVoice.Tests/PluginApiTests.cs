using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Carbon.Plugins;
using Xunit;

namespace OpusVoice.Tests
{
    /// <summary>
    /// Carbon only routes Call()/CallHook()/Interface.Oxide.CallHook() through methods it has cached as hooks:
    /// non-public methods matching a known hook name, or public methods tagged [HookMethod]
    /// (Carbon.Base.BaseHookable.BuildHookCache / Carbon.Hooks.HookCallerInternal.CallHook, verified by decompiling
    /// Carbon). A public method without the attribute is invisible to that system: calling it this way returns null
    /// without ever running the method. This test guards the plugin's cross-plugin API against that trap.
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
        public void ApiMethod_IsPublicAndTaggedHookMethod_SoOtherPluginsCanCallIt(string methodName)
        {
            // SteamVoicePacker lives in the same compiled assembly as Carbon.Plugins.OpusVoice (both come from
            // plugin/OpusVoice.cs via OpusVoice.PluginCheck), and referencing it does not touch any Carbon type.
            string pluginAssemblyPath = typeof(SteamVoicePacker).Assembly.Location;

            using FileStream stream = File.OpenRead(pluginAssemblyPath);
            using var peReader = new PEReader(stream);
            MetadataReader reader = peReader.GetMetadataReader();

            MethodDefinition method = FindMethod(reader, "Carbon.Plugins", "OpusVoice", methodName);

            Assert.True((method.Attributes & MethodAttributes.Public) != 0, $"{methodName} must be public to be called from another plugin's code.");

            bool hasHookMethodAttribute = method.GetCustomAttributes()
                .Select(reader.GetCustomAttribute)
                .Any(ca => AttributeTypeName(reader, ca) == "HookMethodAttribute");
            Assert.True(hasHookMethodAttribute,
                $"{methodName} is public but not tagged [HookMethod], so Carbon's Call()/CallHook() will silently return null instead of running it.");
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

        /// <summary>Name of the type that declares a custom attribute's constructor, e.g. "HookMethodAttribute".</summary>
        private static string AttributeTypeName(MetadataReader reader, CustomAttribute attribute)
        {
            EntityHandle parent = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default,
            };

            return parent.Kind switch
            {
                HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name),
                HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                _ => null,
            };
        }
    }
}
