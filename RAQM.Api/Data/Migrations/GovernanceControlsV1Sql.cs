namespace RAQM.Api.Data.Migrations;

internal static class GovernanceControlsV1Sql
{
    public const string Up = """
CREATE FUNCTION governance.policy_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ApprovedBy" IS NULL AND NEW."ApprovedAt" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."ApprovedAt" IS NULL AND NEW."ApprovedAt" IS NOT NULL AND NEW."ApprovedBy"<>NEW."CreatedBy"
      AND to_jsonb(NEW)-ARRAY['ApprovedAt','ApprovedBy']=to_jsonb(OLD)-ARRAY['ApprovedAt','ApprovedBy']
      AND NOT EXISTS(SELECT 1 FROM governance."Policies" p WHERE p."CompanyId"=NEW."CompanyId" AND p."Duty"=NEW."Duty"
          AND p."ApprovedAt" IS NOT NULL AND (p."Version">=NEW."Version" OR p."EffectiveAt">=NEW."EffectiveAt")) THEN
      RETURN NEW;
   END IF;
 END IF;
 RAISE EXCEPTION 'Governance policy requires independent approval and retained version' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.grant_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ApprovedBy" IS NULL AND NEW."ApprovedAt" IS NULL AND NEW."RevokedAt" IS NULL AND
      NEW."RevokedBy" IS NULL AND NEW."ReviewedBy" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF to_jsonb(NEW)-ARRAY['ApprovedAt','ApprovedBy','RevokedAt','RevokedBy','ReviewedBy']=
        to_jsonb(OLD)-ARRAY['ApprovedAt','ApprovedBy','RevokedAt','RevokedBy','ReviewedBy']
      AND (NEW."ApprovedAt",NEW."ApprovedBy") IS NOT DISTINCT FROM (OLD."ApprovedAt",OLD."ApprovedBy")
      AND ((OLD."RevokedAt" IS NULL AND NEW."RevokedAt" IS NOT NULL AND NEW."RevokedBy" IS NOT NULL
         AND NEW."ReviewedBy" IS NOT DISTINCT FROM OLD."ReviewedBy") OR
        (OLD."ReviewedBy" IS NULL AND NEW."ReviewedBy"<>NEW."UserId" AND NEW."ReviewedBy"<>NEW."ApprovedBy" AND NEW."Emergency"
         AND (NEW."RevokedAt",NEW."RevokedBy") IS NOT DISTINCT FROM (OLD."RevokedAt",OLD."RevokedBy"))) THEN RETURN NEW; END IF;
   IF to_jsonb(NEW)-ARRAY['ApprovedAt','ApprovedBy']=to_jsonb(OLD)-ARRAY['ApprovedAt','ApprovedBy']
      AND OLD."ApprovedAt" IS NULL AND OLD."RevokedAt" IS NULL AND NEW."ApprovedAt" IS NOT NULL
      AND NEW."ApprovedBy"<>NEW."UserId" AND NEW."ApprovedBy"<>NEW."RequestedBy" THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Governance grant history and independent approval are required' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.delegation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ApprovedAt" IS NULL AND NEW."ApprovedBy" IS NULL AND NEW."RevokedAt" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF to_jsonb(NEW)-ARRAY['ApprovedAt','ApprovedBy']=to_jsonb(OLD)-ARRAY['ApprovedAt','ApprovedBy']
      AND OLD."ApprovedAt" IS NULL AND OLD."RevokedAt" IS NULL AND NEW."ApprovedAt" IS NOT NULL
      AND NEW."ApprovedBy"<>NEW."PrincipalId" AND NEW."ApprovedBy"<>NEW."DelegateId" THEN RETURN NEW; END IF;
   IF to_jsonb(NEW)-'RevokedAt'=to_jsonb(OLD)-'RevokedAt' AND OLD."RevokedAt" IS NULL AND NEW."RevokedAt" IS NOT NULL
      THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Delegation requires independent approval and retained evidence' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.authorization_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."State"=0 AND NEW."UsedBy" IS NULL AND length(NEW."IntentHash")=64 AND EXISTS(
      SELECT 1 FROM governance."Policies" p WHERE p."CompanyId"=NEW."CompanyId" AND p."Id"=NEW."PolicyId"
       AND p."Duty"=NEW."Duty" AND p."ApprovedAt" IS NOT NULL AND p."Mode"<>2) THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."State"=0 AND NEW."State" IN(1,2,3)
     AND to_jsonb(NEW)-ARRAY['State','UsedBy']=to_jsonb(OLD)-ARRAY['State','UsedBy'] THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Authorization intent and terminal states are immutable' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.review_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE a governance."Authorizations"%ROWTYPE;
BEGIN
 SELECT * INTO a FROM governance."Authorizations" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."AuthorizationId";
 IF NOT FOUND OR a."State" NOT IN(0,1) OR (a."State"=1 AND NEW."Decision"<>1) OR
    a."MakerId" IN(NEW."ReviewerId",NEW."PrincipalId") OR a."RequestedBy" IN(NEW."ReviewerId",NEW."PrincipalId") OR
    a."ExpiresAt"<=NEW."At" OR
    (NEW."DelegationId" IS NULL AND NEW."ReviewerId"<>NEW."PrincipalId") OR EXISTS(SELECT 1 FROM governance."Reviews" r
      WHERE r."CompanyId"=NEW."CompanyId" AND r."AuthorizationId"=NEW."AuthorizationId"
      AND (r."ReviewerId" IN(NEW."ReviewerId",NEW."PrincipalId") OR r."PrincipalId" IN(NEW."ReviewerId",NEW."PrincipalId"))) THEN
   RAISE EXCEPTION 'Review must use a distinct valid maker-checker identity' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION governance.exception_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ApprovedAt" IS NULL AND NEW."ApprovedBy" IS NULL AND NEW."RevokedAt" IS NULL AND NEW."UsedAt" IS NULL
      AND EXISTS(SELECT 1 FROM governance."Authorizations" a JOIN governance."Policies" p ON p."Id"=a."PolicyId"
        WHERE a."CompanyId"=NEW."CompanyId" AND a."Id"=NEW."AuthorizationId" AND a."Duty"=3 AND p."Mode"=0
        AND p."RulesJson"->>'Waivable'='true' AND NEW."ExpiresAt"<=a."ExpiresAt") THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF to_jsonb(NEW)-ARRAY['ApprovedAt','ApprovedBy']=to_jsonb(OLD)-ARRAY['ApprovedAt','ApprovedBy'] AND OLD."ApprovedAt" IS NULL
      AND OLD."RevokedAt" IS NULL AND NEW."ApprovedAt" IS NOT NULL AND NEW."ApprovedBy"<>NEW."RequestedBy" THEN RETURN NEW; END IF;
   IF to_jsonb(NEW)-'RevokedAt'=to_jsonb(OLD)-'RevokedAt' AND OLD."RevokedAt" IS NULL AND OLD."UsedAt" IS NULL
      AND NEW."RevokedAt" IS NOT NULL THEN RETURN NEW; END IF;
   IF to_jsonb(NEW)-'UsedAt'=to_jsonb(OLD)-'UsedAt' AND OLD."ApprovedAt" IS NOT NULL AND OLD."RevokedAt" IS NULL
      AND OLD."UsedAt" IS NULL AND NEW."UsedAt"<NEW."ExpiresAt" THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Only scoped approved review waivers may be retained and used' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.control_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ApprovedBy" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."ApprovedBy" IS NULL AND NEW."ApprovedBy"<>NEW."CreatedBy"
     AND to_jsonb(NEW)-'ApprovedBy'=to_jsonb(OLD)-'ApprovedBy' THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Control definition requires independent approval and immutable history' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.evaluation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ReviewerId" IS NULL AND NEW."Conclusion" IS NULL AND NEW."ReviewEvidence" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."ReviewerId" IS NULL AND NEW."ReviewerId"<>NEW."TesterId" AND NEW."Conclusion" IS NOT NULL AND NEW."ReviewEvidence" IS NOT NULL
      AND to_jsonb(NEW)-ARRAY['ReviewerId','Conclusion','ReviewEvidence']=to_jsonb(OLD)-ARRAY['ReviewerId','Conclusion','ReviewEvidence']
      AND (NEW."Conclusion"<>0 OR NEW."Outcome"=0 OR EXISTS(SELECT 1 FROM governance."Controls" c JOIN governance."Evidence" e
        ON e."EvaluationId"=NEW."Id" AND e."CompanyId"=NEW."CompanyId" WHERE c."Id"=NEW."ControlId" AND c."Check"=6))
       THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Evaluation and independent review evidence are retained' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.work_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF (NEW."State"=0 OR (NEW."State"=2 AND NEW."Kind"=2 AND NEW."EvaluationId" IS NOT NULL)) AND
      NEW."SubmittedBy" IS NULL AND NEW."ClosedBy" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF to_jsonb(NEW)-ARRAY['State','Version','SubmittedBy','ClosedBy','Response']=
      to_jsonb(OLD)-ARRAY['State','Version','SubmittedBy','ClosedBy','Response']
      AND NEW."Version"<>OLD."Version"
      AND ((OLD."State"=0 AND NEW."State"=1) OR (OLD."State"=1 AND NEW."State"=2) OR
           (OLD."State"=2 AND NEW."State"=3 AND NEW."SubmittedBy"=NEW."OwnerId") OR
           (OLD."State"=3 AND NEW."State"=2) OR (OLD."State"=3 AND NEW."State"=4 AND
              NEW."ClosedBy"<>NEW."OwnerId" AND NEW."ClosedBy"<>NEW."SubmittedBy")) THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Audit work requires valid independent transitions and retained history' USING ERRCODE='23514';
END $$;

CREATE FUNCTION governance.chain_commit_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE expected text;
BEGIN
 IF NEW."Sequence"=1 THEN expected:=repeat('0',64);
 ELSE SELECT e."Hash" INTO expected FROM governance."Events" e WHERE e."CompanyId"=NEW."CompanyId" AND e."Sequence"=NEW."Sequence"-1;
 END IF;
 IF NEW."PreviousHash" IS DISTINCT FROM expected THEN
   RAISE EXCEPTION 'Governance sequence requires the exact preceding hash' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION governance.financial_commit_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE duty integer; policy_id uuid; source_id uuid; actor_id uuid;
BEGIN
 IF TG_TABLE_SCHEMA='accounting' THEN
   IF NEW."State"<>1 OR NEW."SourceModule"<>'ManualJournal' THEN RETURN NEW; END IF;
   duty:=2; source_id:=NEW."Id"; actor_id:=NEW."PostedBy";
 ELSE
   IF NEW."State"<>1 OR NEW."PartyKind"<>1 OR NEW."IsRefund" THEN RETURN NEW; END IF;
   duty:=0; source_id:=NEW."Id"; actor_id:=NEW."CreatedBy";
 END IF;
 SELECT p."Id" INTO policy_id FROM governance."Policies" p WHERE p."CompanyId"=NEW."CompanyId" AND p."Duty"=duty
   AND p."ApprovedAt"<=transaction_timestamp() AND p."EffectiveAt"<=transaction_timestamp()
   ORDER BY p."EffectiveAt" DESC,p."Version" DESC LIMIT 1;
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM governance."Policies" p WHERE p."Id"=policy_id AND p."Mode"=0) THEN RETURN NEW; END IF;
 IF NOT EXISTS(SELECT 1 FROM governance."Events" e JOIN governance."Policies" p ON p."Id"=e."PolicyId"
   WHERE e."CompanyId"=NEW."CompanyId" AND e."Duty"=duty AND e."Type"='GovernanceDecision' AND e."Outcome"=1
      AND e."NextState"='Authorized' AND p."Mode"=0 AND e."PolicyId"=policy_id AND e."ActorId"=actor_id
      AND (duty=2 AND e."JournalId"=source_id OR duty=0 AND e."RelatedId"=source_id)) THEN
   RAISE EXCEPTION 'Active governance requires a matching decision in the financial transaction' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

DO $governance_triggers$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['Policies','Grants','Delegations','Authorizations','Reviews','Exceptions','Events','Controls','Evaluations','Work','Evidence'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON governance.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON governance.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Reviews','Events','Evidence'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON governance.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
END $governance_triggers$;
CREATE TRIGGER b_gov_policy BEFORE INSERT OR UPDATE OR DELETE ON governance."Policies" FOR EACH ROW EXECUTE FUNCTION governance.policy_guard_v1();
CREATE TRIGGER b_gov_grant BEFORE INSERT OR UPDATE OR DELETE ON governance."Grants" FOR EACH ROW EXECUTE FUNCTION governance.grant_guard_v1();
CREATE TRIGGER b_gov_delegation BEFORE INSERT OR UPDATE OR DELETE ON governance."Delegations" FOR EACH ROW EXECUTE FUNCTION governance.delegation_guard_v1();
CREATE TRIGGER b_gov_authorization BEFORE INSERT OR UPDATE OR DELETE ON governance."Authorizations" FOR EACH ROW EXECUTE FUNCTION governance.authorization_guard_v1();
CREATE TRIGGER b_gov_review BEFORE INSERT ON governance."Reviews" FOR EACH ROW EXECUTE FUNCTION governance.review_guard_v1();
CREATE TRIGGER b_gov_exception BEFORE INSERT OR UPDATE OR DELETE ON governance."Exceptions" FOR EACH ROW EXECUTE FUNCTION governance.exception_guard_v1();
CREATE TRIGGER b_gov_control BEFORE INSERT OR UPDATE OR DELETE ON governance."Controls" FOR EACH ROW EXECUTE FUNCTION governance.control_guard_v1();
CREATE TRIGGER b_gov_evaluation BEFORE INSERT OR UPDATE OR DELETE ON governance."Evaluations" FOR EACH ROW EXECUTE FUNCTION governance.evaluation_guard_v1();
CREATE TRIGGER b_gov_work BEFORE INSERT OR UPDATE OR DELETE ON governance."Work" FOR EACH ROW EXECUTE FUNCTION governance.work_guard_v1();
CREATE CONSTRAINT TRIGGER z_gov_chain AFTER INSERT ON governance."Events" DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION governance.chain_commit_v1();
CREATE CONSTRAINT TRIGGER z_gov_journal AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION governance.financial_commit_v1();
CREATE CONSTRAINT TRIGGER z_gov_payment AFTER INSERT OR UPDATE ON trading."Payments" DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION governance.financial_commit_v1();
""";

    public const string Down = """
DROP FUNCTION governance.financial_commit_v1() CASCADE;
DROP FUNCTION governance.chain_commit_v1() CASCADE;
DROP FUNCTION governance.work_guard_v1() CASCADE;
DROP FUNCTION governance.evaluation_guard_v1() CASCADE;
DROP FUNCTION governance.control_guard_v1() CASCADE;
DROP FUNCTION governance.exception_guard_v1() CASCADE;
DROP FUNCTION governance.review_guard_v1() CASCADE;
DROP FUNCTION governance.authorization_guard_v1() CASCADE;
DROP FUNCTION governance.delegation_guard_v1() CASCADE;
DROP FUNCTION governance.grant_guard_v1() CASCADE;
DROP FUNCTION governance.policy_guard_v1() CASCADE;
""";
}
