using Mgs1.Patcher.Core;

namespace Mgs1.Patcher.Gui.Logic;

public static class PatchUserMessages
{
    public static PatchUserMessage ForSelection(DiscSelectionState selection)
    {
        string disc = selection.RequestedDiscId == "disc1" ? "Disco 1" : "Disco 2";
        return selection.Kind switch
        {
            DiscSelectionKind.Ready => new(PatchUserErrorCategory.InputIntegrity, $"{disc} reconhecido com segurança."),
            DiscSelectionKind.Checking => new(PatchUserErrorCategory.InputIntegrity, $"Conferindo o {disc} por hash..."),
            DiscSelectionKind.WrongDisc => new(
                PatchUserErrorCategory.InputIntegrity,
                $"O campo {disc} recebeu exatamente o par do outro disco. Selecione o CUE correto. Os originais não foram alterados e nenhuma saída foi criada."),
            DiscSelectionKind.MixedDiscFiles => new(
                PatchUserErrorCategory.InputIntegrity,
                $"A CUE e a BIN escolhidas para o {disc} pertencem a discos diferentes. Os originais não foram alterados e nenhuma saída foi criada."),
            DiscSelectionKind.CueUnreadable => new(
                PatchUserErrorCategory.InputIntegrity,
                $"Não foi possível localizar com segurança a BIN indicada pelo CUE do {disc}. Os originais não foram alterados e nenhuma saída foi criada."),
            DiscSelectionKind.Unsupported => new(
                PatchUserErrorCategory.InputIntegrity,
                $"A imagem do {disc} não é compatível ou foi modificada. É necessário o par BIN/CUE limpo exato USA Rev 1. Os originais não foram alterados e nenhuma saída foi criada."),
            _ => new(PatchUserErrorCategory.InvalidSelection, $"Selecione o CUE do {disc} para continuar."),
        };
    }

    public static PatchUserErrorCategory Classify(Exception exception)
    {
        string message = exception.Message;
        if (exception is PatcherManifestException)
        {
            return PatchUserErrorCategory.ApplicationPayload;
        }

        if (exception is PatcherIntegrityException)
        {
            return message.Contains("patch", StringComparison.OrdinalIgnoreCase)
                || message.Contains("payload", StringComparison.OrdinalIgnoreCase)
                || message.Contains("BPS", StringComparison.OrdinalIgnoreCase)
                || message.Contains("target CUE", StringComparison.OrdinalIgnoreCase)
                ? PatchUserErrorCategory.ApplicationPayload
                : PatchUserErrorCategory.InputIntegrity;
        }

        if (exception is PatcherSafetyException)
        {
            if (message.Contains("colliding output file names", StringComparison.OrdinalIgnoreCase))
            {
                return PatchUserErrorCategory.OutputNameConflict;
            }

            if (message.Contains("overwrite", StringComparison.OrdinalIgnoreCase))
            {
                return PatchUserErrorCategory.ExistingOutput;
            }

            if (message.Contains("free space", StringComparison.OrdinalIgnoreCase))
            {
                return PatchUserErrorCategory.InsufficientSpace;
            }

            return PatchUserErrorCategory.UnsafePath;
        }

        return PatchUserErrorCategory.UnexpectedIo;
    }

    public static PatchUserMessage For(PatchUserErrorCategory category, IReadOnlyList<string> completedDiscIds)
    {
        string suffix = CompletionSuffix(completedDiscIds);
        string text = category switch
        {
            PatchUserErrorCategory.InputIntegrity => "A imagem selecionada não é compatível ou foi modificada. É necessário o par BIN/CUE limpo exato USA Rev 1.",
            PatchUserErrorCategory.ApplicationPayload => "Os dados de aplicação necessários estão ausentes ou corrompidos. Não é seguro continuar.",
            PatchUserErrorCategory.ExistingOutput => "Já existe uma saída com o nome PT-BR derivado do CUE selecionado. A substituição foi recusada.",
            PatchUserErrorCategory.OutputNameConflict => "Os dois CUEs selecionados resultariam no mesmo nome de saída. Renomeie um dos CUEs originais e tente novamente.",
            PatchUserErrorCategory.InsufficientSpace => "Não há espaço livre suficiente para produzir o par de saída com segurança.",
            PatchUserErrorCategory.UnsafePath => "Um caminho não passou pelas verificações de segurança. A operação foi recusada.",
            PatchUserErrorCategory.Cancelled => "A operação foi cancelada.",
            PatchUserErrorCategory.InvalidSelection => "Selecione e confirme por hash os dois CUEs limpos antes de aplicar a tradução.",
            _ => "Ocorreu um erro de leitura ou gravação inesperado.",
        };
        return new PatchUserMessage(category, $"{text} {suffix}");
    }

    public static PatchUserMessage ForStartupFailure(Exception exception) => new(
        PatchUserErrorCategory.ApplicationPayload,
        "Os dados de aplicação necessários estão ausentes ou corrompidos. Não é seguro continuar. Os originais não foram alterados e nenhuma saída foi criada.");

    private static string CompletionSuffix(IReadOnlyList<string> completedDiscIds)
    {
        if (completedDiscIds.Count == 0)
        {
            return "Os originais não foram alterados e nenhuma saída nova foi criada.";
        }

        string disc = completedDiscIds.Count == 1 ? "Disco 1" : "Discos 1 e 2";
        return $"Os originais não foram alterados. A saída verificada de {disc} permanece na pasta de destino.";
    }
}
