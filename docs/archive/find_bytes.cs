using System;
using System.IO;
using System.Runtime.InteropServices;
public class Program {
    public static void Main() {
        byte[] pe = File.ReadAllBytes(@"C:\Games\NexCore\acclient.exe");
        int peOffset = BitConverter.ToInt32(pe, 0x3C);
        int numSections = BitConverter.ToInt16(pe, peOffset + 6);
        int sectionHeaders = peOffset + 24 + BitConverter.ToInt16(pe, peOffset + 20);
        for (int i = 0; i < numSections; i++) {
            int rva = BitConverter.ToInt32(pe, sectionHeaders + i * 40 + 12);
            int size = BitConverter.ToInt32(pe, sectionHeaders + i * 40 + 16);
            int ptr = BitConverter.ToInt32(pe, sectionHeaders + i * 40 + 20);
            if (rva <= 0x1583F0 && rva + size > 0x1583F0) {
                int fileOffset = ptr + (0x1583F0 - rva);
                Console.WriteLine("Bytes at GetWeenieObject: " + BitConverter.ToString(pe, fileOffset, 32));
            }
        }
    }
}
