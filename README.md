# InfoMail

Dette projekt er en første version af en mail-robot til at overvåge postkassen info@blind.dk og klassificere indkomne mails.

Dette er stadig en prototype og ikke en produktionsklar løsning.

## Funktioner
- Læser mails fra en JSON-kilde som eksempel
- Vurderer om en mail skal videresendes eller sendes til manuel gennemgang
- Logfører beslutninger og grundlaget for hver beslutning

## Struktur
- Services: klassificering og indlæsning af mails
- Models: domænemodeller og beslutninger
- Data: eksempeldata for test

## Kørsel
```bash
dotnet run
```
