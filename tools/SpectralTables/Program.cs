namespace KSARS.SpectralTables
{
    // Offline spectral-table generator: exactly two arguments, <source csv> <output bin>.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: SpectralTables <source.csv> <output.bin>");
                return 2;
            }

            try
            {
                var observer = Cie1931Observer.Load(args[0]);
                var table = ThermalTable.Generate(observer);
                using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write);
                table.Write(output);
                Console.WriteLine(args[1]);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
