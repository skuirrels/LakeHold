import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LandingComponent } from './landing.component';

describe('LandingComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
  });

  it('presents human and agent access as separate topology connections', async () => {
    const fixture = TestBed.createComponent(LandingComponent);
    await fixture.whenStable();

    const topology = fixture.nativeElement.querySelector('.topology') as HTMLElement;
    expect(topology.textContent).toContain('Enterprise SSO');
    expect(topology.textContent).toContain('OIDC identity provider');
    expect(topology.textContent).toContain('MCP Server');
    expect(topology.textContent).toContain('OAuth-secured agent access');
  });

  it('does not publish the unsupported latency claim', async () => {
    const fixture = TestBed.createComponent(LandingComponent);
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).not.toContain('23 ms');
    expect(fixture.nativeElement.textContent).not.toContain('Query latency');
  });

  it('uses the approved Enterprise Platform navigation casing', async () => {
    const fixture = TestBed.createComponent(LandingComponent);
    await fixture.whenStable();

    const link = fixture.nativeElement.querySelector(
      'a[href="/enterprise-data-platform"]',
    ) as HTMLAnchorElement;
    expect(link.textContent?.trim()).toBe('Enterprise Platform');
  });

  it('uses the approved hero banner copy', async () => {
    const fixture = TestBed.createComponent(LandingComponent);
    await fixture.whenStable();

    const heading = fixture.nativeElement.querySelector('.hero h1') as HTMLHeadingElement;
    expect(heading.textContent?.trim()).toBe(
      'LakeHold: an Enterprise LakeHouse, you host yourself',
    );
  });

  it('presents the public developer surface across Java, .NET, Go, and Python', async () => {
    const fixture = TestBed.createComponent(LandingComponent);
    await fixture.whenStable();

    const eyebrow = fixture.nativeElement.querySelector('.hero .eyebrow') as HTMLElement;
    const pillars = fixture.nativeElement.querySelector('.pillars') as HTMLElement;
    const topology = fixture.nativeElement.querySelector('.topology') as HTMLElement;

    expect(eyebrow.textContent?.replace(/\s+/g, ' ').trim()).toContain(
      'PostgreSQL + DuckDB + DuckLake',
    );
    expect(eyebrow.textContent?.replace(/\s+/g, ' ').trim()).toContain('Java · .NET · Go · Python');
    expect(pillars.textContent).toContain('Java · .NET · Go · Python');
    expect(topology.getAttribute('aria-label')).toContain(
      'SQL, Java, .NET, Go, Python, or open Parquet',
    );
  });
});
