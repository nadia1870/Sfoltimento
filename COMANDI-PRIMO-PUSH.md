# Primo push

Dalla cartella che contiene questo file:

```bash
git init
git branch -M main
git add .
git commit -m "Motore di sfoltimento per retention - base V5"
git remote add origin https://github.com/nadia1870/Sfoltimento.git
git push -u origin main
git tag v5-base
git push origin v5-base
```

## Prima di lanciarli, verifica

```bash
git status --short          # appsettings.Development.json NON deve comparire
git diff --cached --stat    # dopo git add, controlla i file inclusi
```

Nessun file deve contenere host o porte reali. La connection string vera va in
`appsettings.Development.json`, partendo dal `.example` fornito:

```bash
copy src\OSM.PaymentOrder.Purge.Host\appsettings.Development.json.example ^
     src\OSM.PaymentOrder.Purge.Host\appsettings.Development.json
```

Quel file è già in `.gitignore`, ma il `.example` contiene l'host reale: se non
lo vuoi nel repository pubblico, sostituiscilo con segnaposto prima del commit.
