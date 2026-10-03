using System;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using UnityEngine.InputSystem;

namespace LCReplay.Plugin
{
    // Instantiate the SDK's LcInputActions subclass only when it is installed.
    // Static inheritance would break reflection-based scans without InputUtils.
    internal static class ReplayKeybinds
    {
        internal static object Create(Assembly sdk, BepInPlugin metadata)
        {
            var baseType = sdk.GetType("LethalCompanyInputUtils.Api.LcInputActions", true)!;
            var attributeType = sdk.GetType("LethalCompanyInputUtils.Api.InputActionAttribute", true)!;
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LCReplay.OptionalInputUtils"), AssemblyBuilderAccess.Run);
            var builder = assembly.DefineDynamicModule("Keybinds").DefineType("LCReplay.BookmarkActions",
                TypeAttributes.Public | TypeAttributes.Sealed, baseType);
            var ctor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(BepInPlugin) });
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, baseType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(BepInPlugin) }, null)!);
            il.Emit(OpCodes.Ret);
            var map = baseType.GetProperty("MapName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetGetMethod(true)!;
            var mapGetter = builder.DefineMethod("get_MapName", MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.SpecialName,
                typeof(string), Type.EmptyTypes);
            il = mapGetter.GetILGenerator(); il.Emit(OpCodes.Ldstr, "Replay"); il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(mapGetter, map);
            var field = builder.DefineField("bookmark", typeof(InputAction), FieldAttributes.Private);
            var property = builder.DefineProperty("AddBookmark", PropertyAttributes.None, typeof(InputAction), null);
            var getter = builder.DefineMethod("get_AddBookmark", MethodAttributes.Public | MethodAttributes.SpecialName, typeof(InputAction), Type.EmptyTypes);
            il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            var setter = builder.DefineMethod("set_AddBookmark", MethodAttributes.Public | MethodAttributes.SpecialName, typeof(void), new[] { typeof(InputAction) });
            il = setter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            property.SetGetMethod(getter); property.SetSetMethod(setter);
            property.SetCustomAttribute(new CustomAttributeBuilder(attributeType.GetConstructor(new[] { typeof(string) })!,
                new object[] { "<Keyboard>/backquote" }, new[] { attributeType.GetProperty("Name")!, attributeType.GetProperty("ActionId")! },
                new object[] { "Add replay bookmark", "AddBookmark" }));
            var loaded = builder.DefineMethod("OnAssetLoaded", MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), Type.EmptyTypes);
            il = loaded.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, baseType.GetMethod("Enable")!); il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(loaded, baseType.GetMethod("OnAssetLoaded")!);
            return Activator.CreateInstance(builder.CreateType()!, new object[] { metadata })!;
        }
    }
}
