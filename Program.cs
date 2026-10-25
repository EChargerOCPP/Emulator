using YKC.Emulator;

// Эмулятор станции YKC для ручной проверки шлюза:
//   dotnet run --project YKC.Emulator -- --host 127.0.0.1 --port 8767 --pile 32010200000001 --guns 2 --vin LSVTEST0000000001 --soc 30
// Команды: plug N, unplug N, stop N (остановка кнопкой на станции),
//   bmsabort N (аварийно BMS, 0x1D), abort N (аварийно станция, 0x21), bmserr N (ошибка BMS 0x1B), quit.
// Старт и стоп удалённо — из админки/приложения, как с обычной станцией.

const string Help = "Команды: plug N, unplug N, stop N, bmsabort N, abort N, bmserr N, quit";

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var host = Arg("host", "127.0.0.1");
var port = int.Parse(Arg("port", "8767"));
var pile = Arg("pile", "32010200000001");
var guns = int.Parse(Arg("guns", "2"));
var ac = Arg("type", "dc").Equals("ac", StringComparison.OrdinalIgnoreCase);
var bms = new BmsProfile();
bms = bms with { Vin = Arg("vin", bms.Vin), StartSoc = int.Parse(Arg("soc", bms.StartSoc.ToString())) };

await using var emulator = new PileEmulator(pile, guns, ac)
{
    Log = line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
    Bms = bms,
};

await emulator.ConnectAsync(host, port);
Console.WriteLine(await emulator.LoggedIn
    ? $"Станция {pile} вошла. {Help}"
    : $"Платформа отказала станции {pile} во входе");

while (Console.ReadLine() is { } line)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;
    var gun = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 1;
    switch (parts[0])
    {
        case "plug": await emulator.PlugAsync(gun); break;
        case "unplug": await emulator.UnplugAsync(gun); break;
        case "stop": await emulator.LocalStopAsync(gun); break;
        case "bmsabort": await emulator.BmsAbortAsync(gun); break;
        case "abort": await emulator.ChargerAbortAsync(gun); break;
        case "bmserr": await emulator.BmsErrorAsync(gun); break;
        case "quit": return;
        default: Console.WriteLine(Help); break;
    }
}
