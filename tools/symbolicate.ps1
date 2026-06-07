# Thin wrapper around symbolicate.py. Forwards all args.
#   .\symbolicate.ps1                      # newest log -> console
#   .\symbolicate.ps1 C:\path\to\some.log  # specific log
#   .\symbolicate.ps1 -o out.txt --only-crash
$py = (Get-Command python -ErrorAction SilentlyContinue)
if (-not $py) { $py = (Get-Command py -ErrorAction SilentlyContinue) }
if (-not $py) { Write-Error "Python not found on PATH."; exit 1 }
& $py.Source (Join-Path $PSScriptRoot "symbolicate.py") @args
