/* DTO shapes (camelCase JSON) mirrored from AML.Application. Only fields used by the UI are typed strictly. */

export interface Me {
  userId: number;
  username: string;
  tenantId?: number;
  roles: string[];
  permissions: string[];
  team?: string | null;
  dataScopeRegion?: string | null;
}

export interface LookupValue {
  id: number;
  category: string;
  code: string;
  labelEn: string;
  labelAr: string;
  sortOrder: number;
  isActive: boolean;
  attribute?: string | null;
}

export interface FieldOption {
  id: number;
  fieldCode: string;
  optionCode: string;
  labelEn: string;
  labelAr: string;
  sortOrder: number;
  isActive: boolean;
}

export interface FormField {
  code: string;
  version: number;
  labelEn: string;
  labelAr: string;
  description?: string | null;
  groupCode: string;
  dataType: string;
  required: boolean;
  isSearchable: boolean;
  isFilterable: boolean;
  isSensitive: boolean;
  validationJson?: string | null;
  defaultValue?: string | null;
  sortOrder: number;
  options: FieldOption[];
}

export interface FormGroup {
  code: string;
  labelEn: string;
  labelAr: string;
  sortOrder: number;
  fieldCodes: string[];
}

export interface FormMetadata {
  customerType: string;
  layoutRole: string;
  layoutVersion: number;
  groups: FormGroup[];
  fields: FormField[];
}

export interface CustomerListItem {
  id: number;
  sourceSystem: string;
  externalId: string;
  customerType: string;
  status: string;
  nameLatin?: string | null;
  nameArabic?: string | null;
  nationalityCountry?: string | null;
  residenceCountry?: string | null;
  riskLevel?: string | null;
  riskScore?: number | null;
  kycStatus: string;
  screeningStatus: string;
  nextReviewDate?: string | null;
  assignedOfficer?: string | null;
  region?: string | null;
  dueDiligenceLevel?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface RiskSummary {
  score: number;
  level: string;
  calculatedScore: number;
  calculatedLevel: string;
  monitoringLevel: string;
  lastAssessmentId?: number | null;
  lastAssessedAt?: string | null;
  nextReviewDate?: string | null;
  overrideApplied: boolean;
  overrideType?: string | null;
  overrideReason?: string | null;
  sanctionsWorkflow: boolean;
  methodologyCode?: string | null;
  methodologyVersion?: number | null;
  reviewOverdue: boolean;
}

export interface CustomerDetail {
  id: number;
  sourceSystem: string;
  externalId: string;
  customerType: string;
  status: string;
  nameLatin?: string | null;
  nameArabic?: string | null;
  dateOfBirth?: string | null;
  nationalityCountry?: string | null;
  residenceCountry?: string | null;
  incorporationCountry?: string | null;
  registrationDate?: string | null;
  kycStatus: string;
  screeningStatus: string;
  assignedOfficer?: string | null;
  region?: string | null;
  dueDiligenceLevel?: string | null;
  isPep: boolean;
  isFrozen: boolean;
  exitedAt?: string | null;
  isTestData: boolean;
  createdAt: string;
  updatedAt: string;
  rowVersion: string;
  identifications: Array<Record<string, unknown>>;
  contacts: Array<Record<string, unknown>>;
  risk?: RiskSummary | null;
  acceptanceDecision?: string | null;
  alertCount: number;
  caseCount: number;
  kycGaps: string[];
  flags: string[];
}

export interface AttributeValue {
  code: string;
  fieldVersion: number;
  labelEn: string;
  labelAr: string;
  groupCode: string;
  dataType: string;
  isSensitive: boolean;
  value: unknown;
  display: string;
  masked: boolean;
  effectiveFrom: string;
  effectiveTo?: string | null;
  source: string;
  verificationStatus: string;
}

export interface AssessmentFactor {
  factorCode: string;
  factorName: string;
  categoryCode: string;
  factorVersion: number;
  dataSource: string;
  dataReference?: string | null;
  observedValue?: string | null;
  dataStatus: string;
  normalizedScore?: number | null;
  weight: number;
  contribution: number;
  included: boolean;
  missingHandling?: string | null;
  explanation: string;
}

export interface Assessment {
  id: number;
  customerId: number;
  methodologyCode: string;
  methodologyVersion: number;
  aggregationMethod: string;
  trigger: string;
  triggerReason?: string | null;
  calculatedScore: number;
  calculatedLevel: string;
  previousScore?: number | null;
  previousLevel?: string | null;
  mandatoryOverrideLevel?: string | null;
  finalLevel: string;
  appliedOverrides: string[];
  appliedFloors: string[];
  sanctionsWorkflow: boolean;
  hasMissingData: boolean;
  explanation: string;
  status: string;
  requiresApproval: boolean;
  approvedBy?: string | null;
  asOf: string;
  calculatedAt: string;
  createdBy: string;
  correlationId?: string | null;
  factors: AssessmentFactor[];
}

export interface Mapping {
  id?: number;
  matchType: string;
  value?: string | null;
  rangeMin?: number | null;
  rangeMax?: number | null;
  score: number;
  priority: number;
  label?: string | null;
}

export interface Factor {
  id: number;
  methodologyId: number;
  code: string;
  name: string;
  categoryCode: string;
  description?: string | null;
  dataSource: string;
  factorType: string;
  weight: number;
  applicableCustomerTypes: string;
  missingDataHandling: string;
  missingDataScore?: number | null;
  freshnessDays?: number | null;
  isActive: boolean;
  version: number;
  rowVersion: string;
  mappings: Mapping[];
}

export interface Threshold {
  id?: number;
  level: string;
  minScore: number;
  maxScore: number;
  reviewMonths: number;
  isSample?: boolean;
}

export interface Methodology {
  id: number;
  code: string;
  name: string;
  version: number;
  status: string;
  aggregationMethod: string;
  cap?: number | null;
  scorePrecision: number;
  appliesToCustomerTypes: string;
  approvalRequiredLevels: string;
  categoryMatrixJson?: string | null;
  effectiveFrom: string;
  effectiveTo?: string | null;
  owner?: string | null;
  rationale?: string | null;
  createdBy?: string | null;
  submittedBy?: string | null;
  approvedBy?: string | null;
  approvedAt?: string | null;
  isSample: boolean;
  rowVersion: string;
  factors: Factor[];
  thresholds: Threshold[];
  overrides: Array<Record<string, unknown> & { id: number }>;
  floors: Array<Record<string, unknown> & { id: number }>;
}

export interface RuleVersion {
  id: number;
  ruleId: number;
  version: number;
  status: string;
  definition: unknown;
  severity: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  rationale?: string | null;
  testEvidence?: string | null;
  expectedImpact?: string | null;
  validationResult?: string | null;
  simulationSummary?: string | null;
  createdBy?: string | null;
  submittedBy?: string | null;
  approvedBy?: string | null;
  publishedBy?: string | null;
  rowVersion: string;
}

export interface Rule {
  id: number;
  code: string;
  name: string;
  description?: string | null;
  category: string;
  ruleType: string;
  severity: string;
  isSample: boolean;
  isRealtimeCapable: boolean;
  typologyCodes?: string | null;
  activeVersionId?: number | null;
  activeVersion?: number | null;
  status: string;
  createdAt: string;
  versions: RuleVersion[];
}

export interface RuleFieldsMeta {
  fields: Array<{ code: string; type: string; operators: string[] }>;
  dynamicFields: Array<{ code: string; type: string }>;
  aggregates: string[];
  operators: string[];
}

export type Row = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
