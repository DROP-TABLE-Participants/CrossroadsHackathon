
<div align="center">

# AI-Native ERP Engine

### Your business shouldn't adapt to an ERP. Your ERP should adapt to your business.

**An AI-native engine that discovers how a company operates, generates a custom ERP, and continuously improves it.**

<!-- Website: add your URL when available. -->

</div>

---

## The problem

ERP implementations are slow, expensive, and heavily dependent on consultants. Before a traditional system can be configured, someone must spend months figuring out how the business actually works.

Our team has seen this firsthand: an ERP project at a company we worked with incurred approximately **€300,000 in losses**.

The problem isn't just implementation. Static software forces companies to change their processes to fit the system, and adapting it later often means another expensive project.

## Our solution

**Our engine builds the ERP around the company—not the other way around.**

The vision is a continuous workflow:

1. **Connect** to the tools and information a company already uses.
2. **Discover** processes by analyzing existing information and interviewing employees.
3. **Generate** an ERP tailored to the company's entities, workflows, permissions, and reports.
4. **Validate and deploy** the generated ERP through a controlled release process.
5. **Improve** the ERP as the business evolves.

```mermaid
flowchart LR
    A[Existing tools and company data] --> B[AI discovery and employee interviews]
    B --> C[Business process model]
    C --> D[ERP generation]
    D --> E[Testing and review]
    E --> F[Company-specific ERP]
    F --> G[Feedback and improvements]
    G --> C
```

## Why this approach?

| Traditional ERP approach | AI-native approach |
| --- | --- |
| Businesses adapt their workflows to a predefined product | An ERP is generated around a company's workflows |
| Consultants manually discover requirements | AI-assisted discovery through systems and employee interviews |
| Customizations are costly to change | Workflows are designed to be regenerated and improved |
| Much of the implementation work starts over for each customer | A shared generation engine can learn from repeated implementations |

Software that builds ERP systems can become better at building the next one.

That's a different product from a fixed ERP with AI features added on top.

## How it works — technical overview

The architecture consists of:

- **Control plane:** Next.js, Payload, and PostgreSQL.
- **ERP runtime:** C# / .NET, multi-tenant application design, and per-tenant PostgreSQL databases.
- **Infrastructure:** Docker, Kubernetes, and Cloudflare.
- **Integrations:** Nango-based connectors, with Google Drive, SharePoint, and Microsoft Teams identified in our product architecture.
- **ERP definition:** Typed YAML bundles describing entities, workflows, permissions, reports, and tests.
- **AI-powered workflow:** Models support discovery, employee interviews, ERP design, review, implementation, and testing, including Playwright-based browser QA.

## Demo

**Demo URL:** Coming soon.

## Why we're building this

We've worked on ERP systems for manufacturers since **2022**, including a previous ERP product used by **50+ paying companies**.

We started this project because we believe the bottleneck in enterprise software is not the absence of ERP features—it's the cost of understanding each company and keeping its software aligned with reality.

## Team

Our team brings experience in ERP development, enterprise infrastructure, AI, and business process implementation.

- **Kalin** · [GitHub](https://github.com/KSChervenkov19)
- **Stoyan Ivanov** · [GitHub](https://github.com/ssivanov19)
- **Mario Berberov** · [GitHub](https://github.com/MCBerberov19)

## Built for what's next

Our ambition is to make custom enterprise software faster to implement, easier to maintain, and accessible to manufacturers that cannot afford years of consulting and rigid systems.

<!-- Add your new website link here when ready. -->

---

<sub>This is an independent project. SAP and Oracle are trademarks of their respective owners; references are for comparison only.</sub>
