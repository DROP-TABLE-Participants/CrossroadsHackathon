# Isolated generated runtime primitives

Only the dedicated Runtime Code Worker may propose `.cs` candidates in this
directory. Candidate files remain data-only records and enums. The live bundle
compiler references this assembly through the protected `Trusted/` generic
compiler seam, which treats candidate instances only as typed data and does not
discover types or invoke candidate-authored methods. A candidate must still pass
the fixed isolated validation profile, independent supervisor review, and a
separate human authorization before staging eligibility. Nothing here grants
merge, deployment, or production authority.
