using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PeNet;
using static Bleak.Etc.Native;
using static Bleak.Etc.Shellcode;
using static Bleak.Etc.Tools;
using static Bleak.Etc.Wrapper;

namespace Bleak.Methods
{
    internal static class ManualMap
    {
        internal static bool Inject(string dllPath, string processName)
        {
            // Ensure parameters are valid

            if (string.IsNullOrEmpty(dllPath) || string.IsNullOrEmpty(processName))
            {
                return false;
            }

            // Ensure the dll exists

            if (!File.Exists(dllPath))
            {
                return false;
            }
            
            // Get the pe headers

            var peHeaders = new PeFile(dllPath);
            
            // Ensure the dll architecture is the same as the compiled architecture

            if (peHeaders.Is64Bit != Environment.Is64BitProcess)
            {
                return false;
            }

            // Get an instance of the specified process

            Process process;

            try
            {
                process = Process.GetProcessesByName(processName)[0];
            }

            catch (IndexOutOfRangeException)
            {
                return false;
            }

            // Inject the dll
            
            return Inject(dllPath, peHeaders, process);
        }

        internal static bool Inject(string dllPath, int processId)
        {
            // Ensure parameters are valid

            if (string.IsNullOrEmpty(dllPath) || processId == 0)
            {
                return false;
            }

            // Ensure the dll exists

            if (!File.Exists(dllPath))
            {
                return false;
            }
            
            // Get the pe headers

            var peHeaders = new PeFile(dllPath);
            
            // Ensure the dll architecture is the same as the compiled architecture

            if (peHeaders.Is64Bit != Environment.Is64BitProcess)
            {
                return false;
            }

            // Get an instance of the specified process

            Process process;

            try
            {
                process = Process.GetProcessById(processId);
            }

            catch (IndexOutOfRangeException)
            {
                return false;
            }

            // Inject the dll
            
            return Inject(dllPath, peHeaders, process);
        }

        private static bool Inject(string dllPath, PeFile peHeaders, Process process)
        {
            // Get a handle to the specified process

            var processHandle = process.SafeHandle;

            if (processHandle == null)
            {
                return false;
            }

            // Get the dll bytes

            var dllBytes = File.ReadAllBytes(dllPath);

            // Pin the dll bytes

            var baseAddress = GCHandle.Alloc(dllBytes, GCHandleType.Pinned);

            // Allocate memory for the dll

            var dllSize = peHeaders.ImageNtHeaders.OptionalHeader.SizeOfImage;
            
            var remoteDllAddress = VirtualAllocEx(processHandle, IntPtr.Zero, (int) dllSize, MemoryAllocation.Commit | MemoryAllocation.Reserve, MemoryProtection.PageExecuteReadWrite);

            // Map the imports

            if (!MapImports(peHeaders, baseAddress.AddrOfPinnedObject()))
            {
                return false;
            }

            // Map the relocations

            if (!MapRelocations(peHeaders, baseAddress.AddrOfPinnedObject(), remoteDllAddress))
            {
                return false;
            }

            // Map the sections

            if (!MapSections(peHeaders, processHandle, baseAddress.AddrOfPinnedObject(), remoteDllAddress))
            {
                return false;
            }

            // Map the tls entries

            if (!MapTlsEntries(peHeaders, processHandle, baseAddress.AddrOfPinnedObject()))
            {
                return false;
            }

            // Call the entry point

            var dllEntryPoint = remoteDllAddress + (int) peHeaders.ImageNtHeaders.OptionalHeader.AddressOfEntryPoint;

            if (!CallEntryPoint(processHandle, remoteDllAddress, dllEntryPoint))
            {
                return false;
            }

            // Unpin the dll bytes

            baseAddress.Free();

            // Free the previously allocated memory
            
            VirtualFreeEx(processHandle, remoteDllAddress, (int) dllSize, MemoryAllocation.Release);
            
            return true;  
        }
        
        private static bool LoadModuleIntoHost(string dllName)
        {
            // Get the dll path

            var dllPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dllName.ToLower());

            // Load the dll into the host process

            return RtlCreateUserThread.Inject(dllPath, Process.GetCurrentProcess().Id);
        }

        private static IntPtr RvaToVa(IntPtr baseAddress, int eLfanew, IntPtr rva)
        {
            // Convert a relative virtual address to a virtual address

            return ImageRvaToVa(baseAddress + eLfanew, baseAddress, rva, IntPtr.Zero);
        }

        private static bool MapImports(PeFile peHeaders, IntPtr baseAddress)
        {
            var eLfanew = (int) peHeaders.ImageDosHeader.e_lfanew;

            // Get the imports

            var imports = peHeaders.ImportedFunctions;

            // Get the import descriptors

            var importDescriptors = peHeaders.ImageImportDescriptors;

            if (importDescriptors == null)
            {
                // No imports

                return true;
            }

            // Group the imports by their dll

            var groupedImports = imports.GroupBy(import => import.DLL);

            // Map the imports

            var descriptorIndex = 0;

            foreach (var dll in groupedImports)
            {
                // Get the function data virtual address

                var functionDataAddress = RvaToVa(baseAddress, eLfanew, (IntPtr) importDescriptors[descriptorIndex].FirstThunk);

                foreach (var import in dll)
                {
                    // Get the proc address

                    var procAddress = GetProcAddress(GetModuleHandle(import.DLL), import.Name);

                    // If the dll isn't already loaded into the host process

                    if (procAddress == IntPtr.Zero)
                    {
                        // Load the dll into the host process

                        if (!LoadModuleIntoHost(import.DLL))
                        {
                            return false;
                        }

                        // Get the proc address

                        procAddress = GetProcAddress(GetModuleHandle(import.DLL), import.Name);
                    }

                    // Map the import

                    Marshal.WriteInt64(functionDataAddress, (long) procAddress);

                    // Next function data virtual address

                    functionDataAddress += Marshal.SizeOf(typeof(IntPtr));
                }

                descriptorIndex += 1;
            }

            return true;
        }

        private static bool MapRelocations(PeFile peHeaders, IntPtr baseAddress, IntPtr remoteAddress)
        {
            // Check if any relocations need to be mapped

            if ((peHeaders.ImageNtHeaders.FileHeader.Characteristics & 0x01) > 0)
            {
                return true;
            }

            var eLfanew = (int) peHeaders.ImageDosHeader.e_lfanew;

            // Calculate the image delta

            var imageDelta = (long) remoteAddress - (long) peHeaders.ImageNtHeaders.OptionalHeader.ImageBase;

            // Get the relocation directory

            var relocationDirectory = peHeaders.ImageRelocationDirectory;

            foreach (var relocation in relocationDirectory)
            {
                // Get the relocation base address
                
                var relocationBaseAddress = RvaToVa(baseAddress, eLfanew, (IntPtr) relocation.VirtualAddress);
                
                // Map the relocations

                foreach (var offset in relocation.TypeOffsets)
                {
                    // Get the relocation address

                    var relocationAddress = relocationBaseAddress + offset.Offset;

                    switch (offset.Type)
                    {
                        case 3:
                        {
                            // If the relocation is Based High Low
                            
                            var value = PointerToStructure<int>(relocationAddress) + (int) imageDelta;
                            
                            Marshal.WriteInt32(relocationAddress, value);

                            break;
                        }

                        case 10:
                        {
                            // If the relocation is Based Dir64

                            var value = PointerToStructure<long>(relocationAddress) + imageDelta;
                            
                            Marshal.WriteInt64(relocationAddress, value);

                            break;
                        }
                    }
                }
            }

            return true;
        }

        private static int GetSectionProtection(DataSectionFlags characteristics)
        {
            var protection = 0;

            // Calculate the sections protection
            
            if (characteristics.HasFlag(DataSectionFlags.MemoryNotCached))
            {
                protection |= (int) MemoryProtection.PageNoCache;
            }
    
            if (characteristics.HasFlag(DataSectionFlags.MemoryExecute))
            {             
                if (characteristics.HasFlag(DataSectionFlags.MemoryRead))
                {          
                    if (characteristics.HasFlag(DataSectionFlags.MemoryWrite))
                    {
                        protection |= (int) MemoryProtection.PageExecuteReadWrite;
                    }

                    else
                    {
                        protection |= (int) MemoryProtection.PageExecuteRead;
                    }

                }

                else if (characteristics.HasFlag(DataSectionFlags.MemoryWrite))
                {
                    protection |= (int) MemoryProtection.PageExecuteWriteCopy;
                }

                else
                {
                    protection |= (int) MemoryProtection.PageExecute;
                }
            }

            else if (characteristics.HasFlag(DataSectionFlags.MemoryRead))
            {
                if (characteristics.HasFlag(DataSectionFlags.MemoryWrite))
                {
                    protection |= (int) MemoryProtection.PageReadWrite;
                }

                else
                {
                    protection |= (int) MemoryProtection.PageReadOnly;
                }
            }

            else if (characteristics.HasFlag(DataSectionFlags.MemoryWrite))
            {
                protection |= (int) MemoryProtection.PageWriteCopy;
            }

            else
            {
                protection |= (int) MemoryProtection.PageNoAccess;
            }

            return protection;
        }

        private static bool MapSections(PeFile peHeaders, SafeHandle processHandle, IntPtr baseAddress, IntPtr remoteAddress)
        {
            // Get the section headers

            var sectionHeaders = peHeaders.ImageSectionHeaders;

            foreach (var section in sectionHeaders)
            {
                // Get the sections protection

                var protection = GetSectionProtection((DataSectionFlags) section.Characteristics);

                // Get the sections address

                var sectionAddress = remoteAddress + (int) section.VirtualAddress;

                // Get the raw data address

                var rawDataAddress = baseAddress + (int) section.PointerToRawData;

                // Get the size of the raw data

                var rawDataSize = (int) section.SizeOfRawData;

                // Get the raw data

                var rawData = new byte[rawDataSize];

                Marshal.Copy(rawDataAddress, rawData, 0, rawDataSize);

                // Map the section

                if (!WriteMemory(processHandle, sectionAddress, rawData, protection))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MapTlsEntries(PeFile peHeaders, SafeHandle processHandle, IntPtr baseAddress)
        {
            // Get the tls callbacks

            PeNet.Structures.IMAGE_TLS_CALLBACK[] tlsCallbacks;

            try
            {
                tlsCallbacks = peHeaders.ImageTlsDirectory.TlsCallbacks;
            }

            catch (NullReferenceException)
            {
                // No tls callbacks
                
                return true;
            }

            // Call the entry point for each tls callback

            return tlsCallbacks.All(callback => CallEntryPoint(processHandle, baseAddress, (IntPtr) callback.Callback));
        }

        private static bool CallEntryPoint(SafeHandle processHandle, IntPtr baseAddress, IntPtr entryPoint)
        {
            // Determine whether compiled as x86 or x64

            var compiledAsx64 = Environment.Is64BitProcess;
            
            // Create shellcode to call the entry point

            var shellcode = compiledAsx64 ? CallDllMainx64(baseAddress, entryPoint) : CallDllMainx86(baseAddress, entryPoint);

            // Allocate memory for the shellcode

            var shellcodeSize = shellcode.Length;

            var shellcodeAddress = VirtualAllocEx(processHandle, IntPtr.Zero, shellcodeSize, MemoryAllocation.Commit | MemoryAllocation.Reserve, MemoryProtection.PageExecuteReadWrite);

            if (shellcodeAddress == IntPtr.Zero)
            {
                return false;
            }

            // Write the shellcode into memory
            
            if (!WriteMemory(processHandle, shellcodeAddress, shellcode))
            {
                return false;
            }

            // Create a user thread to call the entry point in the specified process

            RtlCreateUserThread(processHandle, IntPtr.Zero, false, 0, IntPtr.Zero, IntPtr.Zero, shellcodeAddress, IntPtr.Zero, out var userThreadHandle, IntPtr.Zero);
            
            if (userThreadHandle == IntPtr.Zero)
            {
                return false;
            }

            // Wait for the user thread to finish

            WaitForSingleObject(userThreadHandle, int.MaxValue);

            // Free the previously allocated memory

            VirtualFreeEx(processHandle, shellcodeAddress, shellcodeSize, MemoryAllocation.Release);

            // Close the previously opened handle

            CloseHandle(userThreadHandle);

            return true;
        }
    }
}