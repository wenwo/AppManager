using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace AppManager
{
    // 所有调用均在 Drop 所在 STA 执行；后台线程只接触文件路径。
    internal sealed class ShellImportDrop
    {
        [ComImport, Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAsyncOperation
        {
            void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool asyncMode);
            void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool asyncMode);
            void StartOperation(IntPtr reserved);
            void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inOperation);
            void EndOperation(int result, IntPtr reserved, uint effects);
        }

        private readonly System.Windows.Forms.IDataObject data;
        private readonly System.Runtime.InteropServices.ComTypes.IDataObject native;
        private IAsyncOperation operation;
        private bool ended;
        internal bool Asynchronous { get { return operation != null; } }
        internal DragDropEffects Result { get; private set; }

        internal ShellImportDrop(System.Windows.Forms.IDataObject data)
        {
            this.data = data;
            object original = data;
            // .NET Framework DataObject/OleConverter 包装了原始 OLE 对象；不支持的包装安全回退同步接收。
            for (int depth = 0; depth < 2 && original != null; depth++)
            {
                Type type = original.GetType();
                if (type != typeof(DataObject) && type.FullName != "System.Windows.Forms.DataObject+OleConverter") break;
                FieldInfo field = type.GetField("innerData", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) break;
                original = field.GetValue(original);
            }
            native = original as System.Runtime.InteropServices.ComTypes.IDataObject;
            try
            {
                IAsyncOperation async = original as IAsyncOperation;
                if (async == null) return;
                bool enabled; async.GetAsyncMode(out enabled);
                if (!enabled) return;
                async.StartOperation(IntPtr.Zero);
                operation = async;
            }
            catch (COMException) { }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

        internal void Complete(ImportBatch batch)
        {
            if (ended) return;
            ended = true;
            bool success = batch != null && batch.Committed && batch.Items.Count > 0
                && batch.Items.TrueForAll(i => i.State == FileImport.Copied || i.State == FileImport.Moved || i.State == FileImport.Linked);
            // 优化移动：本程序负责删源，通知来源 NONE，避免来源重复删除。
            uint effect = success && batch.Operation == ImportOperation.Copy ? 1u : 0u;
            Result = (DragDropEffects)effect;
            try
            {
                if (native == null) data.SetData("Performed DropEffect", new MemoryStream(BitConverter.GetBytes(effect)));
                else
                {
                    FORMATETC format = new FORMATETC { cfFormat = unchecked((short)DataFormats.GetFormat("Performed DropEffect").Id),
                        dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
                    STGMEDIUM medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = GlobalAlloc(0x42, new UIntPtr(4)) };
                    if (medium.unionmember == IntPtr.Zero) throw new OutOfMemoryException();
                    try
                    {
                        IntPtr pointer = GlobalLock(medium.unionmember);
                        if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                        try { Marshal.WriteInt32(pointer, (int)effect); } finally { GlobalUnlock(medium.unionmember); }
                        native.SetData(ref format, ref medium, false);
                    }
                    finally { ReleaseStgMedium(ref medium); }
                }
            }
            catch (Exception ex) { Paths.LogError(ex); }
            finally
            {
                if (operation != null)
                {
                    try { operation.EndOperation(success ? 0 : unchecked((int)0x80004005), IntPtr.Zero, effect); }
                    catch (COMException ex) { Paths.LogError(ex); }
                    operation = null;
                }
            }
        }
    }
}
