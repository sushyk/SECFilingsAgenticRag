# General rules

1. No mediocrity allowed. Technical excellence is paramount.

# Coding rules

Code must be:
1. simple:. Write the simplest code that fulfills the requirements. Avoid over-engineering and unnecessary abstractions
2. clean: keep code short and readable. Avoid unnecessary complexity and duplication.
3. clear: code must be self-documenting and self-explanatory. DO NOT add comments explaining what the code is doing. Comments are reserved for explaining why behind the code - what design decision was taken, how it influeces the big picture, non-obvious/missing context, etc - not explaining what the developer can learn by reading the code itself.
4. concise:  Overly verbose code is banned. Every line of code has purpose. If it doesn't, remove it.

# Git Commits

- Write commit messages in the Conventional Commits format: `<type>(<optional scope>): <description>`
- Use the standard types: `feat`, `fix`, `docs`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`. Mark breaking changes with `!` after the type/scope or a `BREAKING CHANGE:` footer.

# Pull Requests

- Every pull request description MUST follow [.github/pull_request_template.md](.github/pull_request_template.md). Read it before running `gh pr create` and fill every section; write "None" or "Not applicable" rather than deleting a section.
