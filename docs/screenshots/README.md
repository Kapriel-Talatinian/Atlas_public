# Screenshot Assets

Ces captures sont utilisées dans le README pour rendre l'UI visible sans lancer le projet localement.

## Current Files

- [market-overview.png](market-overview.png)
- [strategy-lab.png](strategy-lab.png)
- [alpha-lab.png](alpha-lab.png)
- [experimental-bot.png](experimental-bot.png)

## Regeneration Notes

Les captures actuelles ont été générées depuis le frontend local avec des deep links UI:

- `?tab=market&asset=BTC`
- `?tab=strategy&asset=BTC&preset=first`
- `?tab=alpha&asset=ETH`
- `?tab=experimental&asset=SOL`

Pour régénérer proprement:

1. lancer l'API locale
2. builder le frontend avec `VITE_API_BASE_URL` pointant vers cette API
3. servir `frontend/dist`
4. utiliser Playwright CLI `screenshot` sur les URLs ci-dessus
