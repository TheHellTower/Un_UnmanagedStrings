using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using dnlib.IO;
using dnlib.PE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Un_UnmanagedStrings
{
    internal static class Program
    {
        static string filePath = string.Empty, outputFilePath = string.Empty;
        static ModuleDefMD Module = null;
        public static ModuleWriterOptions MWO = null;
        static byte[] stub = null;
        private static void Main(string[] args)
        {
            if (args.Length != 0)
                filePath = args[0].Trim('"');

            while (!File.Exists(filePath))
            {
                Console.WriteLine("File Path: ");
                filePath = Console.ReadLine().Trim('"');
                Console.Clear();
            }

            outputFilePath = filePath.Insert(filePath.Length - 4, "-U_US");

            Module = ModuleDefMD.Load(filePath);

            // Decide target arch from PE machine
            bool isX86 = Program.Module.Machine == Machine.I386;

            stub = isX86
                ? new byte[]
                {
                0x55,             // push ebp
                0x89, 0xE5,       // mov ebp, esp
                0xE8, 0x05, 0, 0, 0, // call +5 (<jump1>)
                0x83, 0xC0, 0x01, // add eax, 1
                // <jump2>:
                0x5D,             // pop ebp
                0xC3,             // ret
                // <jump1>:
                0x58,             // pop eax
                0x83, 0xC0, 0x0B, // add eax, 0x0B
                0xEB, 0xF8        // jmp -8 (<jump2>)
                }
                : new byte[]
                {
                0x48, 0x8D, 0x05, 0x01, 0x00, 0x00, 0x00, // lea rax, [rip+1]
                0xC3                                        // ret
                };

            // Import relevant string constructors to match calls reliably
            Importer importer = new Importer(Module);

            Type sbytePtr = typeof(sbyte).MakePointerType(), charPtr = typeof(char).MakePointerType();

            IMethod stringSbytePtrCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr })!),
                stringCharPtrCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr })!),
                stringSbytePtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr, typeof(int), typeof(int) })!),
                stringCharPtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr, typeof(int), typeof(int) })!);

            // PE reader for RVA -> bytes
            IPEImage pe = Module.Metadata.PEImage;

            // Cache decoded per native Method
            var decodedCache = new Dictionary<uint, string>(); // key by RVA

            int restored = 0;

            // Track candidates for cleanup
            var usedNativeMethods = new HashSet<MethodDef>();

            foreach (TypeDef Type in Module.GetTypes().Where(T => !T.IsGlobalModuleType && T.HasMethods))
                foreach (MethodDef Method in Type.Methods.Where(M => M.HasBody && M.Body.HasInstructions && M.Body.Instructions.Count() > 1))
                {
                    var Instructions = Method.Body.Instructions;

                    for (int i = 0; i < Instructions.Count; i++)
                    {
                        // Pattern start: call <Module>::NativeMethod()
                        if (Instructions[i].OpCode != OpCodes.Call)
                            continue;

                        if (Instructions[i].Operand is not IMethod called)
                            continue;

                        MethodDef native = called.ResolveMethodDef();
                        if (native is null)
                            continue;

                        if (!IsOurNativePointerMethod(native))
                            continue;

                        // Must be followed by a string constructor "new string(...)"
                        if (i + 1 >= Instructions.Count)
                            continue;

                        // Two potential cases in case someone change it:
                        // A) newobj string(ptr)
                        // B) ldc.i4 0 ; ldc.i4 len ; newobj string(ptr,int,int)
                        if (IsNewobj(Instructions[i + 1], stringSbytePtrCtor) || IsNewobj(Instructions[i + 1], stringCharPtrCtor))
                        {
                            bool unicode = ReturnsCharPtr(native);
                            string decoded = DecodeNativeString(pe, native, isX86, unicode, lengthChars: null, decodedCache);

                            // Replace call with ldstr
                            Instructions[i].OpCode = OpCodes.Ldstr;
                            Instructions[i].Operand = decoded;

                            // Remove the newobj
                            Instructions.RemoveAt(i + 1);

                            restored++;
                            usedNativeMethods.Add(native);
                            continue;
                        }

                        // length-based form
                        if (i + 3 < Instructions.Count && IsLdcI4(Instructions[i + 1], out int start) && start == 0 && IsLdcI4(Instructions[i + 2], out int lenChars) && (IsNewobj(Instructions[i + 3], stringSbytePtrLenCtor) || IsNewobj(Instructions[i + 3], stringCharPtrLenCtor)))
                        {
                            bool unicode = ReturnsCharPtr(native);
                            string decoded = DecodeNativeString(pe, native, isX86, unicode, lengthChars: lenChars, decodedCache);

                            // Replace call with ldstr
                            Instructions[i].OpCode = OpCodes.Ldstr;
                            Instructions[i].Operand = decoded;

                            // Remove args + newobj (3 instructions)
                            Instructions.RemoveAt(i + 1);
                            Instructions.RemoveAt(i + 1);
                            Instructions.RemoveAt(i + 1);

                            restored++;
                            usedNativeMethods.Add(native);
                            continue;
                        }
                    }

                    if (restored > 0)
                    {
                        Method.Body.SimplifyBranches();
                        Method.Body.OptimizeBranches();
                    }
                }

            // Remove native methods that were used and are now dead
            CleanupNativeMethods(Module, usedNativeMethods);

            Module.Write(outputFilePath);

            Console.WriteLine($"Restored {restored} strings\nOutput: {Path.GetFileName(outputFilePath)}");
            Console.ReadLine();
        }

        private static void CleanupNativeMethods(ModuleDefMD Module, HashSet<MethodDef> usedNativeMethods)
        {
            TypeDef GT = Module.GlobalType;
            if (GT is null || !GT.HasMethods)
                return;

            // Only remove methods that look like our stub + were actually used (prevents deleting unrelated native methods)
            var toRemove = GT.Methods.Where(M => usedNativeMethods.Contains(M)).ToList();

            foreach (var m in toRemove)
                GT.Methods.Remove(m);
        }

        private static bool IsOurNativePointerMethod(MethodDef Method)
        {
            if (Method.DeclaringType is null || !Method.DeclaringType.IsGlobalModuleType)
                return false;

            // Native / unmanaged / preservesig
            if ((Method.ImplAttributes & MethodImplAttributes.Native) == 0)
                return false;

            // Must return pointer
            return Method.MethodSig?.RetType is PtrSig;
        }

        private static bool ReturnsCharPtr(MethodDef Method)
        {
            if (Method.MethodSig?.RetType is not PtrSig ptr)
                return false;

            return ptr.Next is CorLibTypeSig { ElementType: ElementType.Char };
        }

        private static bool IsNewobj(Instruction Instruction, IMethod ctor) => Instruction.OpCode == OpCodes.Newobj && Instruction.Operand is IMethod op && SameMethod(op, ctor);

        private static bool SameMethod(IMethod a, IMethod b)
        {
            // dnlib compares signatures better via FullName; import differences can exist.
            return a.FullName == b.FullName;
        }

        private static bool IsLdcI4(Instruction ins, out int value)
        {
            //Sexy If-Wall
            value = 0;
            if (ins.OpCode == OpCodes.Ldc_I4) { value = (int)ins.Operand; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_S) { value = (sbyte)ins.Operand; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_0) { value = 0; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_1) { value = 1; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_2) { value = 2; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_3) { value = 3; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_4) { value = 4; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_5) { value = 5; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_6) { value = 6; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_7) { value = 7; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_8) { value = 8; return true; }
            if (ins.OpCode == OpCodes.Ldc_I4_M1) { value = -1; return true; }
            return false;
        }

        private static string DecodeNativeString(IPEImage pe, MethodDef native, bool isX86, bool unicode, int? lengthChars, Dictionary<uint, string> cache)
        {
            uint rva = (uint)native.RVA;
            if (rva == 0)
                throw new InvalidOperationException($"Native Method {native.FullName} has RVA=0 (not patched / not native stub).");

            // Cache by RVA (same Method => same RVA)
            if (cache.TryGetValue(rva, out var s))
                return s;

            // Read from RVA
            var reader = pe.CreateReader(native.NativeBody.RVA);

            // Validate stub
            if (!StartsWith(ref reader, stub))
                throw new InvalidOperationException($"Native Method {native.Name} does not start with expected {(isX86 ? "x86" : "x64")} stub.");

            reader.Position += (uint)stub.Length;

            byte[] raw;

            if (lengthChars.HasValue)
            {
                int byteLen = unicode ? checked(lengthChars.Value * 2) : lengthChars.Value;
                raw = reader.ReadBytes(byteLen);
            }
            else
            {
                // Null-terminated read
                raw = unicode ? ReadUntilDoubleNull(ref reader) : ReadUntilNull(ref reader);
            }

            string decoded = unicode ? Encoding.Unicode.GetString(raw) : Encoding.ASCII.GetString(raw);

            cache[rva] = decoded;
            return decoded;
        }

        private static bool StartsWith(ref DataReader reader, byte[] prefix)
        {
            uint pos = reader.Position;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (reader.ReadByte() != prefix[i])
                {
                    reader.Position = pos;
                    return false;
                }
            }
            reader.Position = pos;
            return true;
        }

        private static byte[] ReadUntilNull(ref DataReader reader)
        {
            var list = new List<byte>(64);
            while (true)
            {
                byte b = reader.ReadByte();
                if (b == 0x00)
                    break;
                list.Add(b);
            }
            return list.ToArray();
        }

        private static byte[] ReadUntilDoubleNull(ref DataReader reader)
        {
            var list = new List<byte>(128);
            while (true)
            {
                byte b1 = reader.ReadByte();
                byte b2 = reader.ReadByte();
                if (b1 == 0x00 && b2 == 0x00)
                    break;
                list.Add(b1);
                list.Add(b2);
            }
            return list.ToArray();
        }
    }
}