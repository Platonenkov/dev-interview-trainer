# Dedupe instructions

You review the planned question stubs of every topic of the .NET stack at once,
together with the inventory of questions that already exist. The stubs were
planned one topic at a time, so the planners could not see each other.

## Input

The item text lists every stub (stubId, topic, level, type, angle,
answerHinge), every topic key with its Covers line, and the existing inventory
(slug and English title).

## Find

1. `duplicate`: two or more stubs, or a stub and an existing question, that
   hinge on the same fact or judgement, even when worded differently or placed
   in different topics. Keep the stub in the topic whose Covers line owns the
   subject; list the stubIds of the group with the one to remove first.
2. `wrong-topic`: a stub whose subject another topic's Covers line owns.
3. `wrong-level`: a stub whose angle does not match its level:
   - junior: definitions and correct everyday use;
   - middle: practical pitfalls and choosing between options;
   - senior: internals, diagnosis, trade-offs under load;
   - lead: technical decisions with team, process and risk;
   - architect: system-wide trade-offs across services and years.

## Replacements

For every stub you remove or move, propose one concrete replacement stub that
keeps the topic and level counts unchanged: same topic and level as the removed
stub, a subject not yet covered, the same type unless the type was the problem.
Follow the outline rules: one testable fact per stub, lowercase kebab-case slug
of at most 80 characters that is not taken, sources only from pages you are
certain exist, plain hyphen `-` only, no time-relative wording.

Report nothing when there is nothing to fix. Do not report stylistic
preferences.
