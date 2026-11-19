using System.Runtime.InteropServices;

namespace MeowFilm.Windows.Tests;

// The native UI Automation client keeps the desktop lyric test's action across the
// process boundary without referencing WPF's managed automation assemblies.
internal static class DesktopAutomation
{
    internal static void Invoke(nint window, string automationId)
    {
        var automation = (IUIAutomation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E"), throwOnError: true)!)!;
        IUIAutomationElement? root = null, target = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationInvokePattern? pattern = null;
        try
        {
            root = automation.ElementFromHandle(window);
            condition = automation.CreatePropertyCondition(30011 /* AutomationId */, automationId);
            target = root.FindFirst(1 | 4 /* Element | Descendants */, condition)
                ?? throw new InvalidOperationException("Separate-process native target is not exposed to automation");
            var invokeInterface = typeof(IUIAutomationInvokePattern).GUID;
            target.GetCurrentPatternAs(10000 /* Invoke */, ref invokeInterface, out pattern);
            pattern.Invoke();
        }
        finally
        {
            if (pattern is not null) Marshal.ReleaseComObject(pattern);
            if (target is not null) Marshal.ReleaseComObject(target);
            if (condition is not null) Marshal.ReleaseComObject(condition);
            if (root is not null) Marshal.ReleaseComObject(root);
            Marshal.ReleaseComObject(automation);
        }
    }

    // Only the interface prefixes needed by this fixture are declared. Unused
    // slots retain their native order; their pointer parameters stay opaque.
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(nint first, nint second, out int same);
        void CompareRuntimeIds(nint first, nint second, out int same);
        void GetRootElement(out nint element);
        IUIAutomationElement ElementFromHandle(nint window);
        void ElementFromPoint(NativePoint point, out nint element);
        void GetFocusedElement(out nint element);
        void GetRootElementBuildCache(nint request, out nint element);
        void ElementFromHandleBuildCache(nint window, nint request, out nint element);
        void ElementFromPointBuildCache(NativePoint point, nint request, out nint element);
        void GetFocusedElementBuildCache(nint request, out nint element);
        void CreateTreeWalker(nint condition, out nint walker);
        void GetControlViewWalker(out nint walker);
        void GetContentViewWalker(out nint walker);
        void GetRawViewWalker(out nint walker);
        void GetRawViewCondition(out nint condition);
        void GetControlViewCondition(out nint condition);
        void GetContentViewCondition(out nint condition);
        void CreateCacheRequest(out nint request);
        void CreateTrueCondition(out nint condition);
        void CreateFalseCondition(out nint condition);
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out nint runtimeId);
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        void FindAll(int scope, nint condition, out nint elements);
        void FindFirstBuildCache(int scope, nint condition, nint request, out nint element);
        void FindAllBuildCache(int scope, nint condition, nint request, out nint elements);
        void BuildUpdatedCache(nint request, out nint element);
        void GetCurrentPropertyValue(int propertyId, out nint value);
        void GetCurrentPropertyValueEx(int propertyId, int ignoreDefault, out nint value);
        void GetCachedPropertyValue(int propertyId, out nint value);
        void GetCachedPropertyValueEx(int propertyId, int ignoreDefault, out nint value);
        void GetCurrentPatternAs(int patternId, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IUIAutomationInvokePattern pattern);
    }

    [ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationInvokePattern
    {
        void Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
}
