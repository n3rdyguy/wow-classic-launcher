// Campo de vision (CVar cameraFov).
//
// El cliente limita cameraFov a 50..90 en el validador del CVar: cualquier otro valor lo devuelve a 90.
// El anti-tamper del cliente hashea el codigo descifrado, asi que modificar ese validador acaba en
// "Security Crash" a los pocos minutos (medido 2026-10-01, dos veces). Por eso aqui NO se toca el codigo:
//   1. Se LEE el codigo para localizar, de forma independiente de la build, el validador (firma de bytes) y,
//      a partir de sus operandos, el global del CVar, el global del propietario de la camara y los offsets.
//   2. Se escriben solo DOS valores en el HEAP (MEM_PRIVATE + PAGE_READWRITE), igual que la clave del servidor:
//        - el valor del CVar (float + int), que es lo que el cliente re-aplica al reconstruir la camara
//        - el fov en radianes del objeto camara, que es lo que usa la proyeccion
//   3. Se vigila cada segundo y se reaplica si el cliente lo reinicia (pantalla de carga, cambio de zona).
//      Al desactivar la opcion se devuelve el valor original.
// El codigo esta cifrado en disco y se descifra pagina a pagina al ejecutarse: el validador solo es legible
// si ya se ejecuto. Por eso Patcher.EnsureConfig escribe `SET cameraFov "90"` en el .wtf cuando la opcion
// esta activa: asi se ejecuta al arrancar y la busqueda encuentra la pagina ya descifrada.
// La camara exige 0 < fov < 180 grados; el deslizador se queda en 60..150.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ForeverLauncher
{
    public sealed class FovPatcher
    {
        public const int MinDegrees = 60, MaxDegrees = 150, DefaultDegrees = 110;

        // Validador de cameraFov (42 bytes; ?? = desplazamientos que cambian con la build):
        //   40 53 48 83 EC 20 48 8B D9 49 8B C8   push rbx; sub rsp,20; mov rbx,rcx; mov rcx,r8
        //   E8 ?? ?? ?? ??                        call parse_float            -> xmm0 = valor nuevo
        //   0F 2F 05 ?? ?? ?? ??                  comiss xmm0,[50.0f]
        //   F3 0F 10 0D ?? ?? ?? ??               movss  xmm1,[90.0f]
        //   72 05  0F 2F C1  76 08                jb reset; comiss xmm0,xmm1; jbe accept
        //   48 8B CB  E8 ?? ?? ?? ??              mov rcx,rbx; call CVar_SetFloat(90)
        //   48 8B 05 ?? ?? ?? ??                  mov rax,[rip+X]             -> global propietario de la camara
        //   48 85 C0  74 11                       test rax,rax; je
        //   48 8B 88 ?? ?? ?? ??                  mov rcx,[rax+camOff]        -> objeto camara
        //   48 85 C9  74 05                       test rcx,rcx; je
        //   E8 ?? ?? ?? ??                        call apply_to_camera
        static readonly byte[] ValidatorSig = Hex(
            "40 53 48 83 EC 20 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 0F 2F 05 ?? ?? ?? ?? F3 0F 10 0D ?? ?? ?? ?? " +
            "72 05 0F 2F C1 76 08 48 8B CB E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 85 C0 74 11 48 8B 88 ?? ?? ?? ?? " +
            "48 85 C9 74 05 E8 ?? ?? ?? ??");
        static readonly bool[] ValidatorMask = Mask(
            "40 53 48 83 EC 20 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 0F 2F 05 ?? ?? ?? ?? F3 0F 10 0D ?? ?? ?? ?? " +
            "72 05 0F 2F C1 76 08 48 8B CB E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 85 C0 74 11 48 8B 88 ?? ?? ?? ?? " +
            "48 85 C9 74 05 E8 ?? ?? ?? ??");
        // offsets dentro de la firma: desplazamiento rip-relativo de 50.0f (0x14) y de 90.0f (0x1C), mov rax (0x2F),
        // disp32 de camOff (0x3E) y call apply (0x47)
        const int OffConst50 = 0x14, OffConst90 = 0x1C, OffOwnerGlobal = 0x2F, OffCamOff = 0x3E, OffApplyCall = 0x47;

        // Dentro de apply_to_camera:
        //   48 8B 05 ?? ?? ?? ??     mov rax,[rip+X]        -> global del CVar cameraFov
        //   48 85 C0 74 ??           test rax,rax; je
        //   F3 0F 10 40 ??           movss xmm0,[rax+valueOff]
        //   F3 0F 59 05 ?? ?? ?? ??  mulss xmm0,[pi/180]
        //   F3 0F 11 41 ??           movss [rcx+fovOff],xmm0
        static readonly byte[] ApplySig = Hex("48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ?? F3 0F 10 40 ?? F3 0F 59 05 ?? ?? ?? ?? F3 0F 11 41 ??");
        static readonly bool[] ApplyMask = Mask("48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ?? F3 0F 10 40 ?? F3 0F 59 05 ?? ?? ?? ?? F3 0F 11 41 ??");
        const int ApplyOffCvarGlobal = 0, ApplyOffValueOff = 16, ApplyOffPiConst = 17, ApplyOffFovOff = 29;

        sealed class Targets
        {
            public long CvarGlobal, OwnerGlobal;
            public int CamOff, ValueOff, FovOff;
            public override string ToString()
            {
                return string.Format("cvar@0x{0:X} owner@0x{1:X} cam+0x{2:X} val+0x{3:X} fov+0x{4:X}", CvarGlobal, OwnerGlobal, CamOff, ValueOff, FovOff);
            }
        }

        readonly int pid;
        readonly Action<string> log;
        readonly Action<Msg> say;
        volatile bool stop;
        Thread thread;
        Targets targets;
        float originalDegrees = float.NaN;    // valor del CVar antes de tocarlo (se restaura al desactivar)
        bool announced;

        public FovPatcher(int pid, Action<string> log, Action<Msg> say)
        {
            this.pid = pid; this.log = log; this.say = say;
        }

        public void Start()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "fov" };
            thread.Start();
        }

        public void Stop() { stop = true; }

        void Loop()
        {
            DateTime nextLocate = DateTime.MinValue;
            bool wasEnabled = false;
            while (!stop)
            {
                try
                {
                    bool enabled = Settings.FovEnabled;
                    if (enabled)
                    {
                        if (targets == null && DateTime.Now >= nextLocate)
                        {
                            targets = Locate();
                            nextLocate = DateTime.Now.AddSeconds(5);
                            if (targets != null) log("fov: " + targets);
                        }
                        if (targets != null) Apply(Settings.FovDegrees);
                    }
                    else if (wasEnabled && targets != null && !float.IsNaN(originalDegrees))
                    {
                        Apply(originalDegrees);
                        log("fov: desactivado, restaurado " + originalDegrees);
                        originalDegrees = float.NaN; announced = false;
                    }
                    wasEnabled = enabled;
                }
                catch (Exception ex)
                {
                    log("fov: " + ex.Message);
                    targets = null;        // el proceso cambio o la memoria se movio: se vuelve a localizar
                    nextLocate = DateTime.Now.AddSeconds(5);
                }
                Thread.Sleep(1000);
            }
        }

        // ------------------------------------------------------------------ localizacion (solo lectura)
        Targets Locate()
        {
            long modBase, modSize;
            using (var p = Process.GetProcessById(pid))
            {
                modBase = p.MainModule.BaseAddress.ToInt64();
                modSize = p.MainModule.ModuleMemorySize;
            }
            IntPtr h = Mem.Open(pid, false);
            try
            {
                var hits = new List<long>();
                foreach (var reg in Mem.Regions(h, modBase, modBase + modSize))
                {
                    if (!reg.Executable) continue;
                    byte[] code = Mem.TryRead(h, reg.Base, (int)reg.Size);
                    if (code == null) continue;
                    int i = -1;
                    while ((i = Find(code, ValidatorSig, ValidatorMask, i + 1)) >= 0)
                    {
                        long va = reg.Base + i;
                        // los dos operandos deben ser exactamente 50.0f y 90.0f: descarta parecidos
                        float c50 = Mem.ReadFloat(h, RipTarget(code, i + OffConst50, va + OffConst50 + 4));
                        float c90 = Mem.ReadFloat(h, RipTarget(code, i + OffConst90, va + OffConst90 + 4));
                        if (c50 == 50f && c90 == 90f) hits.Add(va);
                    }
                }
                if (hits.Count == 0) { log("fov: validador no encontrado (pagina aun cifrada o build distinta)"); return null; }
                if (hits.Count > 1) { log("fov: " + hits.Count + " validadores candidatos; no se hace nada"); return null; }

                long v = hits[0];
                byte[] val = Mem.TryRead(h, v, ValidatorSig.Length);
                var t = new Targets();
                t.OwnerGlobal = RipTarget(val, OffOwnerGlobal + 3, v + OffOwnerGlobal + 7);
                t.CamOff = BitConverter.ToInt32(val, OffCamOff);
                long apply = v + OffApplyCall + 5 + BitConverter.ToInt32(val, OffApplyCall + 1);

                byte[] ap = Mem.TryRead(h, apply, 0x80);
                if (ap == null) { log("fov: apply_to_camera ilegible (pagina cifrada)"); return null; }
                int k = Find(ap, ApplySig, ApplyMask, 0);
                if (k < 0) { log("fov: apply_to_camera no tiene la forma esperada"); return null; }
                float pi180 = Mem.ReadFloat(h, RipTarget(ap, k + ApplyOffPiConst + 4, apply + k + ApplyOffPiConst + 8));
                if (Math.Abs(pi180 - 0.017453292f) > 1e-6f) { log("fov: la constante no es pi/180 (" + pi180 + ")"); return null; }
                t.CvarGlobal = RipTarget(ap, k + ApplyOffCvarGlobal + 3, apply + k + ApplyOffCvarGlobal + 7);
                t.ValueOff = ap[k + ApplyOffValueOff];
                t.FovOff = ap[k + ApplyOffFovOff];
                if (t.CamOff <= 0 || t.CamOff > 0x10000 || t.ValueOff > 0x7F || t.FovOff > 0x7F) { log("fov: offsets fuera de rango"); return null; }
                return t;
            }
            finally { Mem.Close(h); }
        }

        // ------------------------------------------------------------------ escritura (solo heap)
        void Apply(float degrees)
        {
            if (degrees < 1f || degrees > 179f) return;
            IntPtr h = Mem.Open(pid, true);
            try
            {
                long cvar = Mem.ReadPtr(h, targets.CvarGlobal);
                if (cvar == 0) return;                                  // CVar aun no registrado
                long valueAddr = cvar + targets.ValueOff;
                if (!Mem.IsHeap(h, valueAddr, 8)) throw new Exception("el CVar no esta en heap RW");

                float cur = Mem.ReadFloat(h, valueAddr);
                if (float.IsNaN(originalDegrees)) originalDegrees = (cur >= 50f && cur <= 90f) ? cur : 90f;
                bool wrote = false;
                if (cur != degrees)
                {
                    var b = new byte[8];
                    Buffer.BlockCopy(BitConverter.GetBytes(degrees), 0, b, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes((int)degrees), 0, b, 4, 4);
                    Mem.Write(h, valueAddr, b);
                    wrote = true;
                }

                long owner = Mem.ReadPtr(h, targets.OwnerGlobal);
                long cam = owner == 0 ? 0 : Mem.ReadPtr(h, owner + targets.CamOff);
                if (cam != 0)
                {
                    long fovAddr = cam + targets.FovOff;
                    if (!Mem.IsHeap(h, fovAddr, 4)) throw new Exception("la camara no esta en heap RW");
                    float want = (float)(degrees * Math.PI / 180.0);
                    if (Math.Abs(Mem.ReadFloat(h, fovAddr) - want) > 1e-5f)
                    {
                        Mem.Write(h, fovAddr, BitConverter.GetBytes(want));
                        wrote = true;
                    }
                    if (wrote && !announced && Settings.FovEnabled)
                    {
                        announced = true;
                        say(new Msg(MsgKind.Good, "p.fovon", (int)degrees));
                    }
                }
            }
            finally { Mem.Close(h); }
        }

        // ------------------------------------------------------------------ utilidades
        static long RipTarget(byte[] buf, int dispOffset, long nextInstruction)
        {
            return nextInstruction + BitConverter.ToInt32(buf, dispOffset);
        }

        static int Find(byte[] hay, byte[] sig, bool[] mask, int from)
        {
            int last = hay.Length - sig.Length;
            for (int i = Math.Max(0, from); i <= last; i++)
            {
                if (hay[i] != sig[0]) continue;
                int j = 1;
                while (j < sig.Length && (!mask[j] || hay[i + j] == sig[j])) j++;
                if (j == sig.Length) return i;
            }
            return -1;
        }

        static byte[] Hex(string s)
        {
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var b = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) b[i] = parts[i] == "??" ? (byte)0 : Convert.ToByte(parts[i], 16);
            return b;
        }

        static bool[] Mask(string s)
        {
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var m = new bool[parts.Length];
            for (int i = 0; i < parts.Length; i++) m[i] = parts[i] != "??";
            return m;
        }
    }

    // Acceso a memoria para FovPatcher. Lecturas en cualquier region legible; escrituras SOLO en heap RW.
    static class Mem
    {
        const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, PAGE_READWRITE = 4, PAGE_GUARD = 0x100;
        const uint PROCESS_QUERY_INFORMATION = 0x400, PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_VM_OPERATION = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        struct MBI
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State, Protect, Type;
        }
        public struct Region { public long Base, Size; public bool Executable; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] d, UIntPtr n, out UIntPtr r);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] d, UIntPtr n, out UIntPtr w);
        [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr VirtualQueryEx(IntPtr p, IntPtr a, out MBI r, UIntPtr n);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr p);

        public static IntPtr Open(int pid, bool forWrite)
        {
            uint access = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | (forWrite ? PROCESS_VM_WRITE | PROCESS_VM_OPERATION : 0);
            IntPtr p = OpenProcess(access, false, pid);
            if (p == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
            return p;
        }
        public static void Close(IntPtr h) { CloseHandle(h); }

        public static IEnumerable<Region> Regions(IntPtr h, long from, long to)
        {
            long a = from; MBI m;
            while (a < to && VirtualQueryEx(h, new IntPtr(a), out m, new UIntPtr((uint)Marshal.SizeOf(typeof(MBI)))) != UIntPtr.Zero)
            {
                long b = m.BaseAddress.ToInt64(), s = (long)m.RegionSize.ToUInt64();
                if (s <= 0) break;
                bool exec = m.State == MEM_COMMIT && (m.Protect & 0xF0) != 0 && (m.Protect & PAGE_GUARD) == 0;
                yield return new Region { Base = b, Size = Math.Min(s, to - b), Executable = exec };
                a = b + s;
            }
        }

        public static byte[] TryRead(IntPtr h, long a, int n)
        {
            var d = new byte[n]; UIntPtr got;
            if (!ReadProcessMemory(h, new IntPtr(a), d, new UIntPtr((uint)n), out got)) return null;
            if (got.ToUInt64() != (ulong)n) { var t = new byte[(int)got.ToUInt64()]; Buffer.BlockCopy(d, 0, t, 0, t.Length); return t; }
            return d;
        }
        static byte[] Read(IntPtr h, long a, int n)
        {
            var d = TryRead(h, a, n);
            if (d == null || d.Length != n) throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory @0x" + a.ToString("X"));
            return d;
        }
        public static long ReadPtr(IntPtr h, long a) { return BitConverter.ToInt64(Read(h, a, 8), 0); }
        public static float ReadFloat(IntPtr h, long a) { return BitConverter.ToSingle(Read(h, a, 4), 0); }

        public static bool IsHeap(IntPtr h, long a, int n)
        {
            MBI m;
            if (VirtualQueryEx(h, new IntPtr(a), out m, new UIntPtr((uint)Marshal.SizeOf(typeof(MBI)))) == UIntPtr.Zero) return false;
            long end = m.BaseAddress.ToInt64() + (long)m.RegionSize.ToUInt64();
            return m.State == MEM_COMMIT && m.Type == MEM_PRIVATE && m.Protect == PAGE_READWRITE && a + n <= end;
        }

        public static void Write(IntPtr h, long a, byte[] data)
        {
            if (!IsHeap(h, a, data.Length)) throw new Exception("ABORTA: destino fuera de heap MEM_PRIVATE+PAGE_READWRITE");
            UIntPtr w;
            if (!WriteProcessMemory(h, new IntPtr(a), data, new UIntPtr((uint)data.Length), out w) || w.ToUInt64() != (ulong)data.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory");
        }
    }
}
