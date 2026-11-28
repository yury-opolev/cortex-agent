namespace Cortex.Contained.Agent.Host.Evaluation.Labelling;

/// <summary>
/// Calibrated judge rubrics (grading guidance for <see cref="RelevanceJudge"/>). Examples are invented: rubrics are
/// sent to the judge and kept in the public repository, so they must never hold the owner's data.
/// </summary>
public static class JudgeRubrics
{
    /// <summary>
    /// Rubric v3 from the first judge calibration (2026-11-27): "would the searcher want this found", list questions,
    /// plans versus what was done and content-free messages, with invented examples. On the 75 locked items of the
    /// first run it gave claude-opus-5.5 κ 0.605 against 0.587 for <see cref="RelevanceJudge.DefaultRubric"/>
    /// (both judged one item per call). Id <c>sha256:b66795c09b47</c>.
    /// </summary>
    public const string CalibratedV3 = """
        Grade each item by whether a person searching with this query would want this item found.
        2 = the item is from the conversation or note the query is about AND gives the key answer: the fact, reason, explanation or decision asked for. It does not have to cover every detail of the query, use the same words, or be in the same language. If the query asks a main question with extra details, an item that clearly answers the main question is a 2.
        1 = the item is on the same specific topic or from the same conversation, but does not give the answer itself (background, a step on the way, a related detail the query did not ask about).
        0 = a different topic, or the item only shares a word, a name or a general subject with the query.
        When the query asks for a list or for everything of a kind ("which ... did we", "all ..."), an item that gives only one or some of them is 1; it is 2 only when it gives the whole list or most of it.
        A plan, proposal or suggestion is not the answer to a question about what was done or decided (and a report of what was done is not the answer to a question about plans): grade it 1 when it is on the same topic.
        An item with no information of its own (a greeting, "ok", "thanks", a bare request such as "tell me another one") is 0, even when it comes from the same conversation.
        Judge every item on its own text only and do not assume information that is not there. Items and queries may be in any language; a different language is never a reason for a lower grade.
        Examples (invented):
        - Query: "Why did my step count stop syncing?" Item: "The watch app lost its fitness permission after the update, so it no longer uploads data." -> 2 (gives the reason, in other words).
        - Query: "Why did my step count stop syncing, and why is Tuesday's run missing?" Item: "The phone app is not fetching heart and step data yet; I will add that." -> 2 (answers the main question, not every detail).
        - Query: "Which dentist did I book?" Item: "Remind me to call the dentist tomorrow." -> 1 (same topic, no answer).
        - Query: "What is my sister's new address?" Note: "Sister prefers phone calls in the evening." -> 1 (same person and topic area, not the asked fact).
        - Query: "What is my sister's new address?" Note: "The user moved offices last spring." -> 0 (only a similar word).
        - Query: "Hvornår er min tandlægetid?" Item: "Your dentist appointment is on Friday at 9:30." -> 2 (other language, direct answer).
        - Query: "What was the pun about the lazy calendar?" Item: "Haha, give me one more." -> 0 (no information of its own).
        - Query: "Which books did you recommend to me in June?" Item: "Try The Long Road as well." -> 1 (one of several asked for).
        """;
}
