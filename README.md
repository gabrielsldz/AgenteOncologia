# 🔬 Agente de Análise Oncológica com IA

Sistema inteligente para coleta, armazenamento e análise de dados oncológicos do DATASUS usando Inteligência Artificial (Google Gemini).

## 📋 Índice

- [Sobre o Projeto](#sobre-o-projeto)
- [Funcionalidades](#funcionalidades)
- [Requisitos](#requisitos)
- [Instalação](#instalação)
- [Como Usar](#como-usar)
- [Modos de Operação](#modos-de-operação)
- [Estrutura do Projeto](#estrutura-do-projeto)
- [API Key do Gemini](#api-key-do-gemini)
- [Exemplos de Uso](#exemplos-de-uso)

---

## 🎯 Sobre o Projeto

Este sistema realiza web scraping de dados oncológicos do painel do DATASUS, armazena em banco de dados SQLite e oferece uma interface conversacional com IA para análise dos dados coletados.

**Principais características:**

- Coleta automatizada de dados do DATASUS
- Armazenamento em banco SQLite otimizado
- Agente de IA conversacional para análise de dados
- Interface Web e CLI
- Sistema de cache inteligente multi-camadas
- Suporte a diferentes estratégias de coleta

---

## ✨ Funcionalidades

### 📊 Modo Extrator (Scraping)
- Coleta dados oncológicos do DATASUS
- 4 estratégias de coleta configuráveis
- Processamento paralelo com workers configuráveis
- Monitoramento de progresso em tempo real
- Sistema de retry automático

### 🤖 Modo Agente IA
- Análise conversacional dos dados
- Geração automática de queries SQL
- Respostas contextualizadas em linguagem natural
- Histórico de conversação
- Exportação de análises

### 🔍 Modo Consulta Direta
- Consultas SQL diretas ao banco
- Exemplos pré-configurados
- Estatísticas agregadas

### 🌐 Interface Web
- API REST completa
- Interface web interativa
- Sistema de cache multi-camadas
- Endpoints de monitoramento

---

## 📦 Requisitos

### Software Necessário

- **.NET 8.0 SDK** ou superior
  - Download: https://dotnet.microsoft.com/download

- **Sistema Operacional:**
  - Windows, macOS ou Linux

### API Key do Google Gemini

Para usar o Agente IA, você precisa de uma chave de API do Google Gemini (gratuita):

1. Acesse: https://aistudio.google.com/
2. Faça login com sua conta Google
3. Crie uma nova API key
4. Guarde a chave para usar no sistema

---

## 🚀 Instalação

### 1. Clone o repositório

```bash
git clone https://github.com/gabrielsldz/AgenteOncologia.git
cd AgenteOncologia
```

### 2. Restaure as dependências

```bash
dotnet restore
```

### 3. Compile o projeto

```bash
dotnet build
```

---

## 💻 Como Usar

O sistema possui **dois programas principais**:

### 1. Programa CLI (Console)

**Para executar o modo interativo com menu:**

```bash
dotnet run --project ScrapperGranular.csproj
```

Ou usando o arquivo principal:

```bash
dotnet run ProgramGranular.cs
```

**Você verá o menu:**

```
╔════════════════════════════════════════════════════╗
║              SELECIONE O MODO                      ║
╠════════════════════════════════════════════════════╣
║  1. 📊 Modo Extrator (Coletar dados do DATASUS)    ║
║  2. 🤖 Modo Agente IA (Analisar dados existentes)  ║
║  3. 🔍 Modo Consulta (Consultas diretas ao banco)  ║
╚════════════════════════════════════════════════════╝
```

### 2. Interface Web

**Para iniciar o servidor web:**

```bash
dotnet run WebProgram.cs
```

Ou:

```bash
dotnet run --project ScrapperGranular.csproj --launch-profile Web
```

**Acesse no navegador:**
```
http://localhost:5000
```

---

## 🎮 Modos de Operação

### 📊 Modo 1: Extrator (Scraping)

**Quando usar:** Para coletar dados do DATASUS pela primeira vez ou atualizar dados.

**Passo a passo:**

1. Execute o programa CLI
2. Escolha opção `1`
3. Configure os parâmetros:
   - **Ano inicial e final** (2013-2025)
   - **Estratégia de coleta:**
     - `1. Completa` - Todas as combinações (muito lento, milhões de requisições)
     - `2. Hierárquica` - Otimizada em níveis ⭐ **RECOMENDADO**
     - `3. Seletiva` - Foco em dados relevantes (rápido)
     - `4. Incremental` - Apenas dados novos
   - **Workers** (threads paralelas) - Padrão: 16
   - **Timeout** (segundos) - Padrão: 45
   - **Nome do banco** - Padrão: `casos_oncologicos.db`

4. Confirme e aguarde a coleta

**Exemplo de configuração recomendada:**

```
Ano inicial: 2018
Ano final: 2023
Estratégia: 2 (Hierárquica)
Workers: 16
Timeout: 45
Banco: casos_oncologicos.db
```

**Tempo estimado:**
- Estratégia Seletiva (5 anos): ~30-60 minutos
- Estratégia Hierárquica (5 anos): ~2-4 horas
- Estratégia Completa (5 anos): ~10-20 horas

---

### 🤖 Modo 2: Agente IA

**Quando usar:** Para fazer perguntas sobre os dados coletados.

**Passo a passo:**

1. **Certifique-se de que você já coletou dados** (Modo 1 primeiro!)
2. Execute o programa CLI
3. Escolha opção `2`
4. Informe o caminho do banco (Enter para padrão: `casos_oncologicos.db`)
5. **Configure a API Key do Gemini:**
   - Se for a primeira vez, cole sua API key
   - O sistema perguntará se quer salvar no arquivo `.gemini_apikey`
   - Nas próximas execuções, carregará automaticamente

6. Faça suas perguntas!

**Exemplos de perguntas:**

```
💬 Você: Quais os 5 tipos de câncer mais comuns no Brasil?

💬 Você: Compare câncer de mama entre regiões em 2021

💬 Você: Mostre a evolução de câncer de pulmão nos últimos 5 anos

💬 Você: Qual a faixa etária mais afetada por câncer de próstata?
```

**Comandos especiais:**

- `sair` - Sair do modo agente
- `limpar` - Limpar histórico de conversação
- `exportar` - Salvar conversação em arquivo `.txt`

---

### 🔍 Modo 3: Consulta Direta

**Quando usar:** Para consultas SQL diretas sem usar IA.

**Passo a passo:**

1. Execute o programa CLI
2. Escolha opção `3`
3. Informe o caminho do banco
4. Veja exemplos de consultas pré-configuradas

**O sistema mostrará exemplos como:**

- Câncer de mama em mulheres
- Maiores incidências por ano
- Dados específicos por região/sexo/faixa etária

---

### 🌐 Interface Web

**Quando usar:** Para análise interativa via navegador.

**Passo a passo:**

1. **Primeiro, colete dados** usando Modo 1 (CLI)

2. **Inicie o servidor:**
   ```bash
   dotnet run WebProgram.cs
   ```

3. **Abra o navegador:**
   ```
   http://localhost:5000
   ```

4. **Configure a API Key** na interface web

5. **Faça perguntas** no chat

**Endpoints da API:**

```
POST /api/chat              - Conversar com IA
GET  /api/stats             - Estatísticas do banco
GET  /api/health            - Status do sistema
GET  /api/cache/stats/all   - Estatísticas de cache
POST /api/cache/clear/all   - Limpar cache
```

---

## 📁 Estrutura do Projeto

```
ScrapperGranular/
├── AI/                          # Sistema de IA
│   ├── AgentAssistant.cs        # Agente conversacional principal
│   ├── ConversationManager.cs   # Gerenciador de conversas
│   ├── Interfaces/
│   │   └── IAIProvider.cs       # Interface para providers IA
│   ├── Providers/
│   │   └── GeminiProvider.cs    # Implementação Google Gemini
│   └── Cache/                   # Sistema de cache multi-camadas
│       ├── QueryCache.cs        # Cache de respostas
│       ├── SqlResultsCache.cs   # Cache de queries SQL
│       ├── EmbeddingService.cs  # Serviço de embeddings
│       └── TextNormalizer.cs    # Normalização de texto
│
├── Database/                    # Camada de dados
│   └── SqliteHelper.cs          # Helpers SQLite
│
├── Models/                      # Modelos de dados
│   ├── AIResponse.cs
│   ├── Message.cs
│   └── QueryResult.cs
│
├── Utils/                       # Utilitários
│   └── Logger.cs
│
├── wwwroot/                     # Interface Web
│   ├── index.html               # Página principal
│   ├── app.js                   # Lógica do frontend
│   └── style.css                # Estilos
│
├── ProgramGranular.cs           # Programa CLI principal
├── WebProgram.cs                # Programa servidor web
├── ScrapperGranular.csproj      # Configuração do projeto
└── casos_oncologicos.db         # Banco de dados (gerado após coleta)
```

---

## 🔑 API Key do Gemini

### Como obter (Grátis)

1. Acesse: https://aistudio.google.com/
2. Faça login com conta Google
3. Clique em "Get API Key"
4. Crie um novo projeto (se necessário)
5. Copie a chave gerada

### Como configurar

**Opção 1: Arquivo local (recomendado)**

Crie um arquivo `.gemini_apikey` na pasta raiz:

```bash
echo "SUA_API_KEY_AQUI" > .gemini_apikey
```

**Opção 2: Informar manualmente**

O sistema pedirá a chave na primeira execução e oferecerá salvar automaticamente.

**Opção 3: Via interface Web**

Digite a API key no campo apropriado da interface web.

---

## 📊 Exemplos de Uso

### Exemplo 1: Coleta Rápida (Dados Recentes)

```bash
dotnet run ProgramGranular.cs

# Menu: Escolher 1 (Extrator)
Ano inicial: 2020
Ano final: 2023
Estratégia: 3 (Seletiva)
Workers: 16

# Aguardar ~30 minutos
```

### Exemplo 2: Análise com IA

```bash
dotnet run ProgramGranular.cs

# Menu: Escolher 2 (Agente IA)
# Perguntas:

💬 Mostre os 10 tipos de câncer mais comuns em 2022

💬 Compare incidência de câncer de mama entre Norte e Sul

💬 Qual região tem mais casos de câncer de próstata?

💬 Mostre evolução temporal de melanoma
```

### Exemplo 3: Interface Web

```bash
# Terminal 1: Iniciar servidor
dotnet run WebProgram.cs

# Navegador: http://localhost:5000
# Chat:
"Quais os cânceres mais letais por região?"
"Compare faixa etária entre diferentes tipos"
```

---

## 🎯 Estratégias de Coleta Explicadas

### 1. Completa
- Coleta **TODAS** as combinações possíveis
- Ano × Região × Sexo × Faixa Etária × CID
- **Milhões de requisições**
- Use apenas se precisar de dados completos

### 2. Hierárquica ⭐ RECOMENDADO
- Coleta em níveis de granularidade
- Nível 1: Totais gerais
- Nível 2: Por faixa etária
- Nível 3: CIDs comuns detalhados
- Nível 4: CIDs raros apenas totais
- **Otimiza tempo × completude**

### 3. Seletiva
- Foca em dados mais relevantes
- Anos recentes + CIDs comuns
- Anos antigos + apenas CIDs muito comuns
- **Mais rápida, boa para testes**

### 4. Incremental
- Coleta apenas dados que NÃO existem no banco
- **Use para atualizações**
- Requer banco existente

---

## 🛠️ Troubleshooting

### "Banco de dados não encontrado"

**Solução:** Execute primeiro o Modo 1 (Extrator) para coletar dados.

### "API Key inválida"

**Solução:**
1. Verifique se copiou a chave completa
2. Gere uma nova em https://aistudio.google.com/
3. Certifique-se de não ter espaços extras

### "Timeout nas requisições"

**Solução:**
1. Aumente o timeout (default 45s → 60s)
2. Reduza o número de workers (16 → 8)
3. Verifique sua conexão com internet

### "Performance lenta no scraping"

**Solução:**
1. Use estratégia Seletiva ou Hierárquica
2. Ajuste workers baseado no seu hardware
3. Sistema faz garbage collection automático

---

## 📈 Estatísticas e Monitoramento

### CLI

Durante a coleta, você verá:

```
1.234/10.000 (12.3%) | ✓956 ∅123 ✗11 | 15.2 req/s (18.3 atual) ✅ | ETA: 00:45:23
```

- **✓** Requisições bem-sucedidas
- **∅** Requisições vazias (sem dados)
- **✗** Requisições com erro
- **req/s** Taxa de processamento
- **ETA** Tempo estimado restante

### Web

Acesse `/api/cache/stats/all` para ver estatísticas detalhadas do cache.

---

## 🤝 Contribuindo

Contribuições são bem-vindas! Sinta-se livre para:

- Reportar bugs
- Sugerir funcionalidades
- Melhorar documentação
- Enviar pull requests

---

## 📄 Licença

Este projeto é de código aberto para fins educacionais e de pesquisa.

---

## 👥 Autor

Gabriel S.

---

## 🔗 Links Úteis

- **DATASUS:** http://tabnet.datasus.gov.br/
- **Google Gemini API:** https://aistudio.google.com/
- **.NET 8.0:** https://dotnet.microsoft.com/download
- **SQLite:** https://www.sqlite.org/

---

## ❓ FAQ

**P: Preciso pagar pela API do Gemini?**
R: Não, o Google oferece tier gratuito generoso para uso pessoal/pesquisa.

**P: Quanto tempo demora a coleta completa?**
R: Depende da estratégia. Seletiva: ~30min, Hierárquica: ~2-4h, Completa: ~10-20h.

**P: Posso usar outros modelos de IA?**
R: Sim! O código é extensível. Implemente a interface `IAIProvider` para novos providers.

**P: Os dados são atualizados automaticamente?**
R: Não. Use Modo 1 periodicamente ou estratégia Incremental para atualizar.

**P: Posso usar em produção?**
R: O sistema foi projetado para pesquisa. Para produção, revise segurança e rate limits.

---

**Pronto para começar? Execute o comando abaixo e escolha o Modo 1!**

```bash
dotnet run ProgramGranular.cs
```
