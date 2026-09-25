using MedimapReferralLab.Tests;

var root = RepoPaths.FindSolutionRoot();

return args.FirstOrDefault() switch
{
    null or "unit" => UnitChecks.Run(root),
    "live" => await LiveRegression.RunAsync(root, args.Skip(1).ToArray()),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        使い方:
          dotnet run --project MedimapReferralLab.Tests                 オフラインの検査
          dotnet run --project MedimapReferralLab.Tests -- live [--runs N] [--cases K01,K02] [--interval 秒]
                                                                        架空の症例を Azure OpenAI に流して測る
        """);
    return 2;
}
