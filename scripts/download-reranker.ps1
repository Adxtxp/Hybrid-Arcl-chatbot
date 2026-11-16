# Downloads the cross-encoder model the API uses for re-ranking (api/Services/CrossEncoderReranker.cs)
# into api/models/ms-marco-MiniLM-L6-v2/. Run once per machine/server; ~90 MB, gitignored.
#
# Pinned to an exact Hugging Face commit and checked against SHA-256 hashes, so a model update
# upstream can't silently change ranking behavior (or swap in a different file) under you.
#
#   pwsh scripts/download-reranker.ps1        (or: powershell -File scripts\download-reranker.ps1)

$ErrorActionPreference = "Stop"

$repo   = "cross-encoder/ms-marco-MiniLM-L6-v2"
$commit = "233902d25c440f23af6f7d6e94d2946bac0bee0a"
$target = Join-Path $PSScriptRoot "..\api\models\ms-marco-MiniLM-L6-v2"

$files = @(
    @{ Remote = "onnx/model.onnx"; Local = "model.onnx"; Sha256 = "5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a" },
    @{ Remote = "vocab.txt";       Local = "vocab.txt";  Sha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3" }
)

New-Item -ItemType Directory -Force -Path $target | Out-Null

foreach ($f in $files) {
    $dest = Join-Path $target $f.Local
    if ((Test-Path $dest) -and ((Get-FileHash $dest -Algorithm SHA256).Hash -eq $f.Sha256)) {
        Write-Host "  $($f.Local) already present and verified."
        continue
    }
    $url = "https://huggingface.co/$repo/resolve/$commit/$($f.Remote)"
    Write-Host "  Downloading $url ..."
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
    $hash = (Get-FileHash $dest -Algorithm SHA256).Hash
    if ($hash -ne $f.Sha256) {
        Remove-Item $dest
        throw "SHA-256 mismatch for $($f.Local): expected $($f.Sha256), got $hash. File removed."
    }
}

Write-Host "Re-ranker model ready in $((Resolve-Path $target).Path)."
